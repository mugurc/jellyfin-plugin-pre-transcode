using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.PreTranscode.Configuration;

namespace Jellyfin.Plugin.PreTranscode.Encoding;

/// <summary>
/// Applies the profile's output-handling policy to a verified temp output file and returns the final
/// path. The source is only ever deleted for <see cref="OutputHandlingMode.ReplaceInPlace"/>, and only
/// after the new file is safely in place.
/// </summary>
internal static class OutputApplier
{
    private const string DefaultVersionLabel = "Pre-Transcode";

    // Whether two paths denote the same file is a filesystem property: case-insensitive on Windows,
    // case-sensitive on Linux/Docker (the deployment target). Using OrdinalIgnoreCase everywhere treated
    // "/m/Movie.MKV" and "/m/Movie.mkv" as one file on Linux, which could skip the MakeUnique guard and
    // overwrite an unrelated file, or leave the source un-deleted. Ordinal off Windows fixes both.
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static string Apply(EncodingProfile profile, string sourcePath, string tempOutputPath)
    {
        var extension = ContainerExtension(profile.Container);
        return profile.OutputMode switch
        {
            OutputHandlingMode.ReplaceInPlace => ReplaceInPlace(sourcePath, tempOutputPath, extension),
            OutputHandlingMode.AddAsAlternateVersion => AddAsSibling(sourcePath, tempOutputPath, extension, profile.AlternateVersionLabel),
            _ => WriteToSeparateDirectory(profile, sourcePath, tempOutputPath, extension)
        };
    }

    /// <summary>
    /// Maps a container/muxer name to a file extension.
    /// </summary>
    /// <param name="container">The container/muxer name.</param>
    /// <returns>The file extension (with leading dot).</returns>
    public static string ContainerExtension(string container)
    {
        return container.ToLowerInvariant() switch
        {
            "mp4" => ".mp4",
            "matroska" or "mkv" => ".mkv",
            "webm" => ".webm",
            "mov" => ".mov",
            "" => ".mp4",
            _ => "." + SanitizeContainerToken(container)
        };
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
        var extension = ContainerExtension(profile.Container);
        var directory = Path.GetDirectoryName(sourcePath) ?? ".";
        var stem = Path.GetFileNameWithoutExtension(sourcePath);
        return profile.OutputMode switch
        {
            OutputHandlingMode.AddAsAlternateVersion => Path.Combine(directory, stem + " - " + SanitizeLabel(profile.AlternateVersionLabel) + extension),
            OutputHandlingMode.SeparateDirectory => Path.Combine(
                string.IsNullOrWhiteSpace(profile.OutputDirectory) ? directory : profile.OutputDirectory,
                stem + extension),
            _ => null
        };
    }

    private static string ReplaceInPlace(string sourcePath, string tempOutputPath, string extension)
    {
        var directory = Path.GetDirectoryName(sourcePath) ?? ".";
        var stem = Path.GetFileNameWithoutExtension(sourcePath);
        var finalPath = Path.Combine(directory, stem + extension);

        var sameAsSource = string.Equals(finalPath, sourcePath, PathComparison);

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
        // Leading-dot hidden name (matches Jellyfin's "**/.*" ignore glob, so the scratch is never
        // indexed) with a stable prefix. The source's stem is deliberately NOT reused, to keep the name
        // short — re-embedding a long title could push the scratch path past the Windows MAX_PATH limit
        // even when the final path is legal.
        var scratchPath = Path.Combine(
            directory,
            ".pretranscode-tmp-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + extension);
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

        if (!string.Equals(finalPath, sourcePath, PathComparison) && File.Exists(sourcePath))
        {
            File.Delete(sourcePath);
        }

        return finalPath;
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
    private static string AddAsSibling(string sourcePath, string tempOutputPath, string extension, string label)
    {
        var directory = Path.GetDirectoryName(sourcePath) ?? ".";
        var stem = Path.GetFileNameWithoutExtension(sourcePath);
        var finalPath = MakeUnique(Path.Combine(directory, stem + " - " + SanitizeLabel(label) + extension));

        File.Move(tempOutputPath, finalPath);
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

    private static string WriteToSeparateDirectory(EncodingProfile profile, string sourcePath, string tempOutputPath, string extension)
    {
        var directory = string.IsNullOrWhiteSpace(profile.OutputDirectory)
            ? Path.GetDirectoryName(sourcePath) ?? "."
            : profile.OutputDirectory;

        Directory.CreateDirectory(directory);

        var stem = Path.GetFileNameWithoutExtension(sourcePath);
        var finalPath = MakeUnique(Path.Combine(directory, stem + extension));

        File.Move(tempOutputPath, finalPath);
        return finalPath;
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
