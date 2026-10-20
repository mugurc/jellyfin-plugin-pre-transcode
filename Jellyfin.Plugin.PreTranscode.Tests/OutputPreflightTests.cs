using System;
using System.IO;
using Jellyfin.Plugin.PreTranscode.Configuration;
using Jellyfin.Plugin.PreTranscode.Encoding;

namespace Jellyfin.Plugin.PreTranscode.Tests;

// Destination problems used to surface only at the very end: the whole file was encoded, the move into
// place failed, the job was marked Failed — and the next two sweeps did all of it again. Preflight finds
// them before ffmpeg starts.
public class OutputPreflightTests
{
    private static string NewWorkDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pretranscode-pf-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static EncodingProfile Profile(OutputHandlingMode mode, string container = "matroska", string outputDir = "", string label = "Pre-Transcode")
    {
        return new EncodingProfile
        {
            Name = "P",
            Container = container,
            OutputMode = mode,
            OutputDirectory = outputDir,
            AlternateVersionLabel = label
        };
    }

    // Root ignores directory permissions, so on a CI container running as root a read-only directory is
    // still writable. Ask the filesystem rather than assume.
    private static bool CanCreateFileIn(string directory)
    {
        var probe = Path.Combine(directory, "probe-" + Path.GetRandomFileName());
        try
        {
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    [Theory]
    [InlineData(OutputHandlingMode.ReplaceInPlace)]
    [InlineData(OutputHandlingMode.AddAsAlternateVersion)]
    [InlineData(OutputHandlingMode.SeparateDirectory)]
    public void WritableDestination_IsFine_AndLeavesNothingBehind(OutputHandlingMode mode)
    {
        var work = NewWorkDir();
        try
        {
            var source = Path.Combine(work, "Movie.mp4");
            File.WriteAllText(source, "original");

            Assert.Null(OutputApplier.Preflight(Profile(mode), source));
            Assert.Equal(new[] { source }, Directory.GetFileSystemEntries(work));
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    // A replace-in-place to an extension Jellyfin does not index deletes the original and leaves a file
    // the library cannot see; an alternate version under such a name never appears at all. Both must be
    // refused before any work is done.
    [Theory]
    [InlineData(OutputHandlingMode.ReplaceInPlace)]
    [InlineData(OutputHandlingMode.AddAsAlternateVersion)]
    public void ContainerJellyfinDoesNotIndexAsVideo_IsRefused(OutputHandlingMode mode)
    {
        var work = NewWorkDir();
        try
        {
            var source = Path.Combine(work, "Recording.ts");
            File.WriteAllText(source, "original");

            var reason = OutputApplier.Preflight(Profile(mode, "nut"), source);

            Assert.NotNull(reason);
            Assert.Contains(".nut", reason, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    // A separate output directory is documented as living OUTSIDE the libraries, and nothing is deleted in
    // this mode, so an export-only container is the admin's call.
    [Fact]
    public void ContainerJellyfinDoesNotIndexAsVideo_IsAllowedForASeparateDirectory()
    {
        var work = NewWorkDir();
        try
        {
            var source = Path.Combine(work, "Recording.ts");
            File.WriteAllText(source, "original");

            Assert.Null(OutputApplier.Preflight(Profile(OutputHandlingMode.SeparateDirectory, "nut", Path.Combine(work, "export")), source));
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    // The jellyfin user lacking write permission, or a Docker ":ro" library mount.
    [Theory]
    [InlineData(OutputHandlingMode.ReplaceInPlace)]
    [InlineData(OutputHandlingMode.AddAsAlternateVersion)]
    [InlineData(OutputHandlingMode.SeparateDirectory)]
    public void UnwritableDestination_IsRefused(OutputHandlingMode mode)
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Unix permission bits; Windows ACLs are not modelled here.
        }

        var work = NewWorkDir();
        var media = Path.Combine(work, "media");
        Directory.CreateDirectory(media);
        try
        {
            var source = Path.Combine(media, "Movie.mkv");
            File.WriteAllText(source, "original");
            File.SetUnixFileMode(media, UnixFileMode.UserRead | UnixFileMode.UserExecute);

            var reason = OutputApplier.Preflight(Profile(mode, "mp4"), source);

            if (CanCreateFileIn(media))
            {
                Assert.Null(reason); // running as root: the directory really is writable
            }
            else
            {
                Assert.NotNull(reason);
                Assert.Contains(media, reason, StringComparison.Ordinal);
            }

            Assert.Equal(new[] { source }, Directory.GetFileSystemEntries(media));
        }
        finally
        {
            File.SetUnixFileMode(media, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.Delete(work, recursive: true);
        }
    }

    // The apply step creates a missing output directory, so preflight does too rather than refusing it.
    [Fact]
    public void SeparateDirectory_CreatesTheOutputDirectory()
    {
        var work = NewWorkDir();
        try
        {
            var source = Path.Combine(work, "Movie.mkv");
            var outDir = Path.Combine(work, "out", "nested");
            File.WriteAllText(source, "original");

            Assert.Null(OutputApplier.Preflight(Profile(OutputHandlingMode.SeparateDirectory, "mp4", outDir), source));
            Assert.True(Directory.Exists(outDir));
            Assert.Empty(Directory.GetFileSystemEntries(outDir));
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public void SeparateDirectory_ThatCannotBeCreated_IsRefused()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Unix permission bits; Windows ACLs are not modelled here.
        }

        var work = NewWorkDir();
        var locked = Path.Combine(work, "locked");
        Directory.CreateDirectory(locked);
        try
        {
            var source = Path.Combine(work, "Movie.mkv");
            var outDir = Path.Combine(locked, "out");
            File.WriteAllText(source, "original");
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserExecute);

            var reason = OutputApplier.Preflight(Profile(OutputHandlingMode.SeparateDirectory, "mp4", outDir), source);

            if (CanCreateFileIn(locked))
            {
                Assert.Null(reason); // running as root
            }
            else
            {
                Assert.NotNull(reason);
                Assert.Contains(outDir, reason, StringComparison.Ordinal);
            }
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.Delete(work, recursive: true);
        }
    }

    // "<source stem> - <label>.<ext>" can pass the 255-byte name limit when a long episode title meets a
    // long label; the move then fails after the whole encode, on every sweep.
    [Fact]
    public void AlternateVersionNameTooLongForTheFilesystem_IsRefused()
    {
        var work = NewWorkDir();
        try
        {
            var source = Path.Combine(work, new string('t', 120) + ".mkv");
            File.WriteAllText(source, "original");

            var reason = OutputApplier.Preflight(Profile(OutputHandlingMode.AddAsAlternateVersion, "mkv", label: new string('l', 140)), source);

            Assert.NotNull(reason);
            Assert.Contains("255", reason, StringComparison.Ordinal);
            Assert.Contains("label", reason, StringComparison.Ordinal);
            Assert.Equal(new[] { source }, Directory.GetFileSystemEntries(work));
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public void AlternateVersionNameJustWithinTheLimit_IsFine()
    {
        var work = NewWorkDir();
        try
        {
            // 100 + " - " + 148 + ".mkv" = 255.
            var source = Path.Combine(work, new string('t', 100) + ".mkv");
            File.WriteAllText(source, "original");

            Assert.Null(OutputApplier.Preflight(Profile(OutputHandlingMode.AddAsAlternateVersion, "mkv", label: new string('l', 148)), source));
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    // Linux and macOS limit a name to 255 UTF-8 BYTES, Windows to 255 UTF-16 units, so a title in a
    // non-Latin script reaches the limit at well under 255 characters off Windows.
    [Fact]
    public void FileNameLength_CountsWhatTheFilesystemCounts()
    {
        var name = new string('ü', 100) + ".mkv";

        Assert.Equal(OperatingSystem.IsWindows() ? 104 : 204, OutputApplier.FileNameLength(name));
    }

    [Fact]
    public void RemovesStaleScratchInTheTargetDirectory()
    {
        var work = NewWorkDir();
        try
        {
            var source = Path.Combine(work, "Movie.mkv");
            var stale = Path.Combine(work, ".pretranscode-tmp-0123456789abcdef0123456789abcdef.mp4");
            File.WriteAllText(source, "original");
            File.WriteAllText(stale, "half a copy");
            File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-2));
            var logger = new CapturingLogger();

            Assert.Null(OutputApplier.Preflight(Profile(OutputHandlingMode.ReplaceInPlace, "mp4"), source, logger));

            Assert.False(File.Exists(stale));
            Assert.Contains(logger.Entries, e => e.Message.Contains(stale, StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }
}
