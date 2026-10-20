using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Emby.Naming.Common;
using Jellyfin.Plugin.PreTranscode.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PreTranscode.Encoding;

/// <summary>
/// Applies the profile's output-handling policy to a verified temp output file and returns the final
/// path. The source is only ever deleted for <see cref="OutputHandlingMode.ReplaceInPlace"/>, and only
/// after the new file is safely in place.
/// </summary>
internal static class OutputApplier
{
    private const string DefaultVersionLabel = "Pre-Transcode";

    // Every scratch and probe file this class creates beside the media starts with this. The leading dot
    // matches Jellyfin's "**/.*" ignore glob, so none is ever indexed, and the fixed prefix is what lets a
    // later run recognise one that a killed process left behind.
    private const string ScratchPrefix = ".pretranscode-tmp-";

    // NAME_MAX on ext4, XFS, Btrfs, ZFS, APFS and NTFS alike (counted per FileNameLength).
    private const int MaxFileNameLength = 255;

    // Whether two paths denote the same file is a filesystem property: case-insensitive on Windows,
    // case-sensitive on Linux/Docker (the deployment target). Using OrdinalIgnoreCase everywhere treated
    // "/m/Movie.MKV" and "/m/Movie.mkv" as one file on Linux, which could skip the MakeUnique guard and
    // overwrite an unrelated file, or leave the source un-deleted. Ordinal off Windows fixes both.
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    // Jellyfin's own list, from the server's Emby.Naming at runtime, so this is what the running scanner
    // actually indexes rather than a copy that can drift from it.
    private static readonly Lazy<HashSet<string>> VideoExtensions = new(
        () => new HashSet<string>(new NamingOptions().VideoFileExtensions, StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Gets how long a scratch file must have gone unwritten before it counts as debris. A copy in
    /// progress keeps advancing its write time, but SMB can report the creation time until the handle
    /// closes and a multi-gigabyte copy over a slow link takes hours, so the margin is generous: removing
    /// a live scratch would only fail that job's move (the original is never touched before the move
    /// succeeds), while keeping a dead one a few hours longer costs nothing.
    /// </summary>
    internal static TimeSpan StaleScratchAge { get; } = TimeSpan.FromHours(6);

    public static string Apply(EncodingProfile profile, string sourcePath, string tempOutputPath, ILogger? logger = null)
    {
        return Apply(profile, sourcePath, tempOutputPath, logger, File.Delete);
    }

    // deleteSource is a seam for the one failure no test filesystem can stage portably: an original that
    // refuses to be deleted after its replacement is already in place.
    internal static string Apply(EncodingProfile profile, string sourcePath, string tempOutputPath, ILogger? logger, Action<string> deleteSource)
    {
        var extension = ContainerExtension(profile.Container);
        return profile.OutputMode switch
        {
            OutputHandlingMode.ReplaceInPlace => ReplaceInPlace(sourcePath, tempOutputPath, extension, logger, deleteSource),
            OutputHandlingMode.AddAsAlternateVersion => AddAsSibling(sourcePath, tempOutputPath, extension, profile.AlternateVersionLabel, logger),
            _ => WriteToSeparateDirectory(profile, sourcePath, tempOutputPath, extension, logger)
        };
    }

    /// <summary>
    /// Maps a container/muxer name to the file extension its output is written under.
    /// </summary>
    /// <param name="container">The container/muxer name.</param>
    /// <returns>The file extension (with leading dot).</returns>
    public static string ContainerExtension(string container)
    {
        // ffmpeg's muxer names are not extensions, and the extension is all Jellyfin's scanner looks at.
        // Appending the name as-is wrote an mpegts profile's output as "Recording.mpegts", which Jellyfin
        // does not index, and replace-in-place then deleted the Recording.ts it replaced — the item
        // dropped out of the library. Each name here maps to an extension in Jellyfin's video list ("ogg"
        // to .ogv, which every version indexes as video, while 12 files .ogg under audio). Anything else
        // keeps its own name, and Preflight refuses it where the library would not see the result.
        return container.ToLowerInvariant() switch
        {
            "" or "mp4" or "psp" => ".mp4",
            "matroska" or "mkv" => ".mkv",
            "webm" => ".webm",
            "mov" => ".mov",
            "mpegts" => ".ts",
            "ogg" => ".ogv",
            "ipod" => ".m4v",
            "dvd" or "svcd" or "vcd" => ".mpg",
            _ => "." + SanitizeContainerToken(container)
        };
    }

    /// <summary>
    /// Whether Jellyfin's library scanner indexes a file with this extension as a video.
    /// </summary>
    /// <param name="extension">The extension, with its leading dot.</param>
    /// <returns><c>true</c> when Jellyfin treats such a file as video.</returns>
    internal static bool IsIndexedAsVideo(string extension)
    {
        return VideoExtensions.Value.Contains(extension);
    }

    // A container value ultimately becomes part of a file path, so strip it to bare alphanumerics: an
    // admin-set (or migrated) value like "mp4/../../etc" must not be able to traverse out of the target
    // directory. Falls back to mp4 if nothing usable remains.
    private static string SanitizeContainerToken(string container)
    {
        var cleaned = new StringBuilder(container.Length);
        foreach (var c in container.ToLowerInvariant())
        {
            if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9'))
            {
                cleaned.Append(c);
            }
        }

        return cleaned.Length == 0 ? "mp4" : cleaned.ToString();
    }

    /// <summary>
    /// Checks, before anything is encoded, that the apply step will be able to put the result where this
    /// profile sends it.
    /// <para>
    /// Each of these used to surface only at the very end: the whole file was encoded, the move into place
    /// failed — a read-only Docker mount, a directory the jellyfin user cannot write, a version name past
    /// the 255-byte limit — the job failed, and the next two sweeps did all of it again. The executor asks
    /// before it probes or encodes; the evaluator asks before it queues.
    /// </para>
    /// </summary>
    /// <param name="profile">The encoding profile.</param>
    /// <param name="sourcePath">The source file path.</param>
    /// <param name="logger">Where to report stale scratch files removed along the way, if anywhere.</param>
    /// <returns><c>null</c> when nothing stands in the way; otherwise the reason, worded for the admin.</returns>
    public static string? Preflight(EncodingProfile profile, string sourcePath, ILogger? logger = null)
    {
        var extension = ContainerExtension(profile.Container);

        // A separate output directory is documented as living outside the libraries, and that mode deletes
        // nothing, so an export-only container is the admin's business there. The other two put the result
        // in the library: replace-in-place would delete the original in favour of a file the scanner cannot
        // see, and an alternate version would simply never appear.
        if (profile.OutputMode is OutputHandlingMode.ReplaceInPlace or OutputHandlingMode.AddAsAlternateVersion
            && !IsIndexedAsVideo(extension))
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "profile '{0}' writes container '{1}' as '{2}' files, which Jellyfin does not index as video, so {3}; choose a container such as matroska or mp4",
                profile.Name,
                profile.Container,
                extension,
                profile.OutputMode == OutputHandlingMode.ReplaceInPlace
                    ? "replacing in place would delete the original and the item would drop out of the library"
                    : "the new version would never appear in the library");
        }

        var (directory, fileName) = PlannedOutput(profile, sourcePath, extension);

        var length = FileNameLength(fileName);
        if (length > MaxFileNameLength)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "the output file name '{0}' is {1} {2} long, over the {3} the filesystem allows; {4}",
                fileName,
                length,
                OperatingSystem.IsWindows() ? "characters" : "bytes",
                MaxFileNameLength,
                profile.OutputMode == OutputHandlingMode.AddAsAlternateVersion
                    ? "shorten the profile's alternate-version label"
                    : "shorten the source file name");
        }

        try
        {
            // The apply step creates a missing output directory, so this does too rather than refusing it.
            if (profile.OutputMode is not (OutputHandlingMode.ReplaceInPlace or OutputHandlingMode.AddAsAlternateVersion))
            {
                Directory.CreateDirectory(directory);
            }

            RemoveStaleScratch(directory, logger);

            var probe = NewScratchPath(directory, ".probe");
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
            }

            TryDelete(probe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return "cannot write to '" + directory + "': " + ex.Message;
        }

        return null;
    }

    /// <summary>
    /// The length a filesystem compares against its 255 limit: Linux and macOS count UTF-8 bytes, Windows
    /// counts UTF-16 units — so a title in a non-Latin script reaches the limit well before 255
    /// characters off Windows.
    /// </summary>
    /// <param name="fileName">A file name, without directory.</param>
    /// <returns>Its length in the filesystem's own units.</returns>
    internal static int FileNameLength(string fileName)
    {
        return OperatingSystem.IsWindows() ? fileName.Length : System.Text.Encoding.UTF8.GetByteCount(fileName);
    }

    /// <summary>
    /// Whether two paths denote the same file, using the filesystem's own case rules — case-insensitive
    /// on Windows, case-sensitive on Linux/Docker. Callers that compare a computed output path against a
    /// source path must use this rather than a fixed comparison: <c>OrdinalIgnoreCase</c> would treat
    /// <c>/m/Movie.MKV</c> and <c>/m/Movie.mkv</c> as one file on Linux, where they are two.
    /// </summary>
    /// <param name="a">The first path.</param>
    /// <param name="b">The second path.</param>
    /// <returns><c>true</c> when both paths denote the same file.</returns>
    internal static bool IsSameFile(string? a, string? b)
    {
        return string.Equals(a, b, PathComparison);
    }

    /// <summary>
    /// Whether this profile's output for this source resolves to the source file itself — a "separate
    /// directory" profile with no output directory (so the output lands beside the source) whose
    /// container maps to the extension the source already has, or a source that already sits in the
    /// configured output directory.
    /// <para>
    /// Such a job cannot do anything useful: <see cref="WriteToSeparateDirectory"/> will not overwrite
    /// the source, so it falls back to <see cref="MakeUnique"/> and writes "Movie (1).mkv" beside it,
    /// which Jellyfin indexes as a second movie. Both entry points — the evaluator (sweeps, the
    /// item-added monitor) and the executor (which a manually-queued job reaches <em>without</em> passing
    /// through the evaluator) — must refuse it, so the test lives here rather than in either of them.
    /// </para>
    /// </summary>
    /// <param name="profile">The encoding profile.</param>
    /// <param name="sourcePath">The source file path.</param>
    /// <returns><c>true</c> when the profile would write its output over its own source.</returns>
    internal static bool WritesOverItsOwnSource(EncodingProfile profile, string sourcePath)
    {
        var expected = ExpectedOutputPath(profile, sourcePath);
        return expected is not null && IsSameFile(expected, sourcePath);
    }

    /// <summary>
    /// The primary output path this profile would first write for the given source (before any
    /// uniqueness suffix), or <c>null</c> for <see cref="OutputHandlingMode.ReplaceInPlace"/> where no
    /// distinct sibling/target is created. Used to detect — independently of the job queue — that a
    /// source has already been transcoded, so repeated sweeps don't redo it.
    /// </summary>
    /// <param name="profile">The encoding profile.</param>
    /// <param name="sourcePath">The source file path.</param>
    /// <returns>The expected output path, or <c>null</c>.</returns>
    internal static string? ExpectedOutputPath(EncodingProfile profile, string sourcePath)
    {
        if (profile.OutputMode is not (OutputHandlingMode.AddAsAlternateVersion or OutputHandlingMode.SeparateDirectory))
        {
            return null;
        }

        var (directory, fileName) = PlannedOutput(profile, sourcePath, ContainerExtension(profile.Container));
        return Path.Combine(directory, fileName);
    }

    // The directory the output is written into and the name it first tries there, before any uniqueness
    // suffix. Shared by ExpectedOutputPath and Preflight, so what is checked is what gets written.
    private static (string Directory, string FileName) PlannedOutput(EncodingProfile profile, string sourcePath, string extension)
    {
        var directory = Path.GetDirectoryName(sourcePath) ?? ".";
        var stem = Path.GetFileNameWithoutExtension(sourcePath);
        return profile.OutputMode switch
        {
            OutputHandlingMode.ReplaceInPlace => (directory, stem + extension),
            OutputHandlingMode.AddAsAlternateVersion => (directory, stem + " - " + SanitizeLabel(profile.AlternateVersionLabel) + extension),
            _ => (string.IsNullOrWhiteSpace(profile.OutputDirectory) ? directory : profile.OutputDirectory, stem + extension)
        };
    }

    private static string ReplaceInPlace(string sourcePath, string tempOutputPath, string extension, ILogger? logger, Action<string> deleteSource)
    {
        var directory = Path.GetDirectoryName(sourcePath) ?? ".";
        var stem = Path.GetFileNameWithoutExtension(sourcePath);
        var finalPath = Path.Combine(directory, stem + extension);

        // An extension that differs from the source's only by case (camera footage: GOPR0001.MP4 under an
        // mp4 profile) is not a container change, so the source is replaced under its own name. Comparing
        // the names ordinally made them two files: on a case-insensitive filesystem (APFS, SMB/CIFS, exFAT)
        // File.Exists then found the source itself, the output became "GOPR0001 (1).mp4", the original
        // was deleted and GOPR0001.srt matched nothing any more. Writing over the source's own name is
        // right on every filesystem — where both spellings exist as two files, only the source is touched.
        var sameAsSource = string.Equals(Path.GetExtension(sourcePath), extension, StringComparison.OrdinalIgnoreCase);
        if (sameAsSource)
        {
            finalPath = sourcePath;
        }

        // Never destroy a pre-existing *different* file. When the container changes (e.g. movie.mkv ->
        // movie.mp4) an unrelated movie.mp4 may already sit next to the source; only the source itself
        // may be replaced, so anything else is given a unique name instead of being overwritten.
        if (!sameAsSource && File.Exists(finalPath))
        {
            finalPath = MakeUnique(finalPath);
        }

        // Bring the finished output onto the source's own volume under a scratch name first. When the
        // temp dir and the media live on different mounts (the norm under Docker) File.Move is a
        // non-atomic copy+delete; performing that copy to a throwaway name means a crash partway through
        // leaves the original completely untouched. Only once the copy has fully succeeded is the result
        // swapped into place with a same-volume (atomic) rename, and only then is the source removed —
        // so there is never a window where the original is gone and the replacement is incomplete.
        // The scratch name is deliberately short rather than built from the source's stem: re-embedding a
        // long title could push the scratch path past the Windows MAX_PATH limit even when the final path
        // is legal.
        RemoveStaleScratch(directory, logger);
        var scratchPath = NewScratchPath(directory, extension);
        try
        {
            File.Move(tempOutputPath, scratchPath);
            File.Move(scratchPath, finalPath, overwrite: true);
        }
        catch
        {
            TryDelete(scratchPath);
            throw;
        }

        // From here on the replacement is in place, so a source that will not go is the admin's to remove,
        // not a failed job. Windows refuses to delete a file a player has open (Jellyfin opens media
        // without FileShare.Delete) and CIFS can refuse too; throwing marked the job Failed with both files
        // on disk and skipped the library repoint, and every later sweep re-encoded the original into
        // "Movie (1).mp4". Returning the new path lets the job complete and the repoint run, and the
        // completed record is what keeps the evaluator off the leftover.
        if (!sameAsSource && File.Exists(sourcePath))
        {
            try
            {
                deleteSource(sourcePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger?.LogWarning(
                    ex,
                    "Replaced {Source} with {Output}, but the original could not be deleted (it may be open in a player, or the share refused). Both files are now on disk; delete {Leftover} manually",
                    sourcePath,
                    finalPath,
                    sourcePath);
            }
        }

        return finalPath;
    }

    private static string NewScratchPath(string directory, string suffix)
    {
        return Path.Combine(directory, ScratchPrefix + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + suffix);
    }

    /// <summary>
    /// Deletes scratch files in <paramref name="directory"/> that nobody has written to for
    /// <see cref="StaleScratchAge"/>.
    /// <para>
    /// A restart or <c>docker stop</c> (SIGKILL after ten seconds) during a multi-minute cross-volume copy
    /// leaves a partial, often multi-gigabyte scratch beside the media, and nothing else ever removes it —
    /// startup only clears the plugin's own temp directory. Age is what separates debris from a copy still
    /// in progress, such as a concurrent job's in the same season folder. Housekeeping only: it never
    /// throws.
    /// </para>
    /// </summary>
    /// <param name="directory">The directory a scratch file is about to be created in.</param>
    /// <param name="logger">Where to report what was removed, if anywhere.</param>
    internal static void RemoveStaleScratch(string directory, ILogger? logger)
    {
        var cutoffUtc = DateTime.UtcNow - StaleScratchAge;
        try
        {
            // AttributesToSkip = 0: .NET reports a dot-file as Hidden on Unix, and skips Hidden by default.
            var options = new EnumerationOptions
            {
                AttributesToSkip = 0,
                IgnoreInaccessible = true,
                MatchCasing = MatchCasing.CaseSensitive,
                RecurseSubdirectories = false
            };

            foreach (var path in Directory.EnumerateFiles(directory, ScratchPrefix + "*", options))
            {
                try
                {
                    var info = new FileInfo(path);
                    if (info.LastWriteTimeUtc > cutoffUtc)
                    {
                        continue;
                    }

                    var bytes = info.Length;
                    info.Delete();
                    logger?.LogInformation(
                        "Removed {Path} ({MegaBytes} MB), a partial copy left behind by an earlier Pre-Transcode run that was interrupted",
                        path,
                        bytes / (1024 * 1024));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger?.LogWarning(ex, "Could not remove the stale Pre-Transcode scratch file {Path}", path);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger?.LogDebug(ex, "Could not look for stale Pre-Transcode scratch files in {Directory}", directory);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // Names the output "<original> - <label>.<ext>", a distinct name next to the source. NOTE: Jellyfin's
    // filename-based version grouping applies to MOVIES only (a version file must begin with the movie's
    // own folder name); for TV EPISODES it does nothing, so the two files are grouped into one item purely
    // by the database link that AlternateVersionMerger sets, not by this name. The label is what Jellyfin
    // shows in the version selector once the link is in place.
    private static string AddAsSibling(string sourcePath, string tempOutputPath, string extension, string label, ILogger? logger)
    {
        var directory = Path.GetDirectoryName(sourcePath) ?? ".";
        var stem = Path.GetFileNameWithoutExtension(sourcePath);
        var finalPath = MakeUnique(Path.Combine(directory, stem + " - " + SanitizeLabel(label) + extension));

        MoveIntoPlace(tempOutputPath, finalPath, logger);
        return finalPath;
    }

    private static string SanitizeLabel(string label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return DefaultVersionLabel;
        }

        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(label.Trim().Where(c => !invalid.Contains(c)).ToArray()).Trim();
        return string.IsNullOrEmpty(cleaned) ? DefaultVersionLabel : cleaned;
    }

    private static string WriteToSeparateDirectory(EncodingProfile profile, string sourcePath, string tempOutputPath, string extension, ILogger? logger)
    {
        var directory = string.IsNullOrWhiteSpace(profile.OutputDirectory)
            ? Path.GetDirectoryName(sourcePath) ?? "."
            : profile.OutputDirectory;

        Directory.CreateDirectory(directory);

        var stem = Path.GetFileNameWithoutExtension(sourcePath);
        var finalPath = MakeUnique(Path.Combine(directory, stem + extension));

        MoveIntoPlace(tempOutputPath, finalPath, logger);
        return finalPath;
    }

    // Moves a finished temp output to its final path crash-safely. The temp dir is usually on a different
    // mount from the media (the norm under Docker), so File.Move there is a non-atomic copy+delete: a
    // crash mid-copy would leave a half-written file at the final, library-visible name. Copying to a
    // hidden scratch on the destination volume first (matches Jellyfin's "**/.*" ignore glob, so a partial
    // scratch is never indexed) and only then renaming it into place — a same-volume atomic operation —
    // means the final name only ever appears complete.
    private static void MoveIntoPlace(string tempOutputPath, string finalPath, ILogger? logger)
    {
        var directory = Path.GetDirectoryName(finalPath) ?? ".";
        RemoveStaleScratch(directory, logger);
        var scratchPath = NewScratchPath(directory, Path.GetExtension(finalPath));
        try
        {
            File.Move(tempOutputPath, scratchPath);
            File.Move(scratchPath, finalPath);
        }
        catch
        {
            TryDelete(scratchPath);
            throw;
        }
    }

    private static string MakeUnique(string path)
    {
        if (!File.Exists(path))
        {
            return path;
        }

        var directory = Path.GetDirectoryName(path) ?? ".";
        var stem = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var i = 1; i < 1000; i++)
        {
            var candidate = Path.Combine(directory, stem + " (" + i.ToString(CultureInfo.InvariantCulture) + ")" + extension);
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        return path;
    }
}
