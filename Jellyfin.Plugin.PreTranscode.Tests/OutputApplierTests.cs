using System;
using System.IO;
using System.Linq;
using Emby.Naming.Common;
using Jellyfin.Plugin.PreTranscode.Configuration;
using Jellyfin.Plugin.PreTranscode.Encoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PreTranscode.Tests;

public class OutputApplierTests
{
    private static string NewWorkDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pretranscode-oa-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static EncodingProfile Profile(
        OutputHandlingMode mode,
        string container = "mp4",
        string outputDir = "",
        string label = "Pre-Transcode")
    {
        return new EncodingProfile
        {
            Container = container,
            OutputMode = mode,
            OutputDirectory = outputDir,
            AlternateVersionLabel = label
        };
    }

    [Fact]
    public void SeparateDirectory_WritesToDir_LeavesSourceUntouched()
    {
        var work = NewWorkDir();
        try
        {
            var source = Path.Combine(work, "src.mkv");
            var temp = Path.Combine(work, "tmp.mp4");
            var outDir = Path.Combine(work, "out");
            File.WriteAllText(source, "original");
            File.WriteAllText(temp, "encoded");

            var final = OutputApplier.Apply(Profile(OutputHandlingMode.SeparateDirectory, "mp4", outDir), source, temp);

            Assert.StartsWith(outDir, final);
            Assert.True(File.Exists(final));
            Assert.True(File.Exists(source), "source must be left untouched");
            Assert.False(File.Exists(temp), "temp must be moved");
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public void ReplaceInPlace_ReplacesSource_WithNewExtension()
    {
        var work = NewWorkDir();
        try
        {
            var source = Path.Combine(work, "movie.mkv");
            var temp = Path.Combine(work, "tmp.mp4");
            File.WriteAllText(source, "original");
            File.WriteAllText(temp, "encoded");

            var final = OutputApplier.Apply(Profile(OutputHandlingMode.ReplaceInPlace, "mp4"), source, temp);

            Assert.Equal(Path.Combine(work, "movie.mp4"), final);
            Assert.True(File.Exists(final));
            Assert.False(File.Exists(source), "original (different extension) must be removed");
            Assert.Equal("encoded", File.ReadAllText(final));
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public void AddAsAlternateVersion_KeepsBothFiles()
    {
        var work = NewWorkDir();
        try
        {
            var source = Path.Combine(work, "movie.mkv");
            var temp = Path.Combine(work, "tmp.mp4");
            File.WriteAllText(source, "original");
            File.WriteAllText(temp, "encoded");

            var final = OutputApplier.Apply(Profile(OutputHandlingMode.AddAsAlternateVersion, "mp4"), source, temp);

            Assert.True(File.Exists(source), "source must remain");
            Assert.True(File.Exists(final));
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public void AddAsAlternateVersion_UsesJellyfinVersionNaming_WithConfiguredLabel()
    {
        var work = NewWorkDir();
        try
        {
            var source = Path.Combine(work, "movie.mkv");
            var temp = Path.Combine(work, "tmp.mp4");
            File.WriteAllText(source, "original");
            File.WriteAllText(temp, "encoded");

            var final = OutputApplier.Apply(
                Profile(OutputHandlingMode.AddAsAlternateVersion, "mp4", label: "H.264 1080p"), source, temp);

            // "<base> - <label>.<ext>" is what Jellyfin groups into one item with a version selector.
            Assert.Equal(Path.Combine(work, "movie - H.264 1080p.mp4"), final);
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public void AddAsAlternateVersion_StripsInvalidFileNameCharactersFromLabel()
    {
        var work = NewWorkDir();
        try
        {
            var source = Path.Combine(work, "movie.mkv");
            var temp = Path.Combine(work, "tmp.mp4");
            File.WriteAllText(source, "original");
            File.WriteAllText(temp, "encoded");

            var final = OutputApplier.Apply(
                Profile(OutputHandlingMode.AddAsAlternateVersion, "mp4", label: "H.264/1080p"), source, temp);

            Assert.Equal(work, Path.GetDirectoryName(final));
            Assert.DoesNotContain("/", Path.GetFileName(final));
            Assert.True(File.Exists(final));
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public void AddAsAlternateVersion_BlankLabel_FallsBackToDefault()
    {
        var work = NewWorkDir();
        try
        {
            var source = Path.Combine(work, "movie.mkv");
            var temp = Path.Combine(work, "tmp.mp4");
            File.WriteAllText(source, "original");
            File.WriteAllText(temp, "encoded");

            var final = OutputApplier.Apply(
                Profile(OutputHandlingMode.AddAsAlternateVersion, "mp4", label: "   "), source, temp);

            Assert.Equal(Path.Combine(work, "movie - Pre-Transcode.mp4"), final);
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public void ExpectedOutputPath_AlternateVersion_MatchesActualNaming()
    {
        var movies = Path.Combine(Path.GetTempPath(), "movies");
        var src = Path.Combine(movies, "movie.mkv");
        var expected = OutputApplier.ExpectedOutputPath(Profile(OutputHandlingMode.AddAsAlternateVersion, "mkv", label: "H.264 1080p"), src);
        Assert.Equal(Path.Combine(movies, "movie - H.264 1080p.mkv"), expected);
    }

    [Fact]
    public void ExpectedOutputPath_SeparateDirectory_UsesConfiguredDirOrSourceDir()
    {
        var movies = Path.Combine(Path.GetTempPath(), "movies");
        var outDir = Path.Combine(Path.GetTempPath(), "out");
        var src = Path.Combine(movies, "movie.mkv");
        Assert.Equal(Path.Combine(outDir, "movie.mp4"),
            OutputApplier.ExpectedOutputPath(Profile(OutputHandlingMode.SeparateDirectory, "mp4", outDir), src));
        Assert.Equal(Path.Combine(movies, "movie.mp4"),
            OutputApplier.ExpectedOutputPath(Profile(OutputHandlingMode.SeparateDirectory, "mp4", string.Empty), src));
    }

    [Fact]
    public void ExpectedOutputPath_ReplaceInPlace_IsNull()
    {
        var src = Path.Combine(Path.GetTempPath(), "movies", "movie.mkv");
        Assert.Null(OutputApplier.ExpectedOutputPath(Profile(OutputHandlingMode.ReplaceInPlace, "mp4"), src));
    }

    [Fact]
    public void ContainerExtension_SanitizesPathTraversalAttempt()
    {
        var ext = OutputApplier.ContainerExtension("mp4/../../etc");
        Assert.DoesNotContain("/", ext);
        Assert.DoesNotContain("..", ext);
        Assert.DoesNotContain("\\", ext);
        Assert.Equal(".mp4etc", ext);
    }

    [Fact]
    public void ReplaceInPlace_DoesNotClobberUnrelatedExistingFile()
    {
        var work = NewWorkDir();
        try
        {
            // movie.mkv is being replaced with an mp4, but an unrelated movie.mp4 already sits beside it.
            var source = Path.Combine(work, "movie.mkv");
            var bystander = Path.Combine(work, "movie.mp4");
            var temp = Path.Combine(work, "tmp.mp4");
            File.WriteAllText(source, "original");
            File.WriteAllText(bystander, "keep me");
            File.WriteAllText(temp, "encoded");

            var final = OutputApplier.Apply(Profile(OutputHandlingMode.ReplaceInPlace, "mp4"), source, temp);

            Assert.True(File.Exists(bystander), "an unrelated existing file must never be destroyed");
            Assert.Equal("keep me", File.ReadAllText(bystander));
            Assert.NotEqual(bystander, final);
            Assert.Equal("encoded", File.ReadAllText(final));
            Assert.False(File.Exists(source), "the source itself is still replaced (removed)");
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public void ReplaceInPlace_LeavesNoScratchFileBehind()
    {
        var work = NewWorkDir();
        try
        {
            var source = Path.Combine(work, "movie.mkv");
            var temp = Path.Combine(work, "tmp.mkv");
            File.WriteAllText(source, "original");
            File.WriteAllText(temp, "encoded");

            OutputApplier.Apply(Profile(OutputHandlingMode.ReplaceInPlace, "mkv"), source, temp);

            Assert.DoesNotContain(
                Directory.GetFiles(work),
                f => Path.GetFileName(f).StartsWith(".pretranscode-tmp", System.StringComparison.Ordinal));
            Assert.Equal("encoded", File.ReadAllText(source));
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public void SeparateDirectory_NeverClobbersSource_WhenSameDirAndExtension()
    {
        var work = NewWorkDir();
        try
        {
            var source = Path.Combine(work, "a.mkv");
            var temp = Path.Combine(work, "tmp.mkv");
            File.WriteAllText(source, "original");
            File.WriteAllText(temp, "encoded");

            // Empty output dir => alongside source; same extension must not overwrite the source.
            var final = OutputApplier.Apply(Profile(OutputHandlingMode.SeparateDirectory, "mkv", string.Empty), source, temp);

            Assert.NotEqual(source, final);
            Assert.True(File.Exists(source));
            Assert.Equal("original", File.ReadAllText(source));
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    // ffmpeg's muxer names are not file extensions. Appending the muxer name made an mpegts profile write
    // "Recording.mpegts" — an extension Jellyfin does not index — and replace-in-place then deleted the
    // Recording.ts it was replacing, so the item dropped out of the library. Every mapping must land on
    // an extension Jellyfin's own scanner treats as video.
    [Theory]
    [InlineData("mpegts", ".ts")]
    [InlineData("MPEGTS", ".ts")]
    [InlineData("ogg", ".ogv")]
    [InlineData("ipod", ".m4v")]
    [InlineData("psp", ".mp4")]
    [InlineData("dvd", ".mpg")]
    [InlineData("svcd", ".mpg")]
    [InlineData("vcd", ".mpg")]
    [InlineData("matroska", ".mkv")]
    [InlineData("mp4", ".mp4")]
    [InlineData("", ".mp4")]
    public void ContainerExtension_MapsMuxerNamesToExtensionsJellyfinIndexesAsVideo(string container, string expected)
    {
        var extension = OutputApplier.ContainerExtension(container);

        Assert.Equal(expected, extension);
        Assert.Contains(extension, new NamingOptions().VideoFileExtensions, StringComparer.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(".mkv", true)]
    [InlineData(".MP4", true)]
    [InlineData(".ts", true)]
    [InlineData(".ogv", true)]
    [InlineData(".nut", false)]
    [InlineData(".ismv", false)]
    [InlineData(".ogg", false)] // Jellyfin files .ogg under audio
    public void IsIndexedAsVideo_FollowsJellyfinsOwnExtensionList(string extension, bool expected)
    {
        Assert.Equal(expected, OutputApplier.IsIndexedAsVideo(extension));
    }

    [Fact]
    public void ReplaceInPlace_MpegtsProfile_ReplacesTheRecordingUnderItsOwnName()
    {
        var work = NewWorkDir();
        try
        {
            var source = Path.Combine(work, "Recording.ts");
            var temp = Path.Combine(work, "tmp.ts");
            File.WriteAllText(source, "original");
            File.WriteAllText(temp, "encoded");

            var final = OutputApplier.Apply(Profile(OutputHandlingMode.ReplaceInPlace, "mpegts"), source, temp);

            Assert.Equal(source, final);
            Assert.Equal("encoded", File.ReadAllText(source));
            Assert.Equal(new[] { "Recording.ts" }, Directory.GetFiles(work).Select(Path.GetFileName));
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    // Windows will not delete a file a player has open (Jellyfin opens media without FileShare.Delete),
    // and CIFS can refuse as well. By then Movie.mp4 is already in place, so throwing marked the job
    // Failed with both files on disk, skipped the library repoint, and the next sweep re-encoded the
    // original into "Movie (1).mp4" — up to three times. The replacement succeeded; the leftover original
    // is a warning for the admin, not a failure.
    [Fact]
    public void ReplaceInPlace_OriginalThatCannotBeDeleted_StillReturnsTheNewFile_AndSaysSo()
    {
        var work = NewWorkDir();
        try
        {
            var source = Path.Combine(work, "Movie.mkv");
            var temp = Path.Combine(work, "tmp.mp4");
            File.WriteAllText(source, "original");
            File.WriteAllText(temp, "encoded");
            var logger = new CapturingLogger();

            var final = OutputApplier.Apply(
                Profile(OutputHandlingMode.ReplaceInPlace, "mp4"),
                source,
                temp,
                logger,
                _ => throw new IOException("The process cannot access the file because it is being used by another process."));

            Assert.Equal(Path.Combine(work, "Movie.mp4"), final);
            Assert.Equal("encoded", File.ReadAllText(final));
            Assert.True(File.Exists(source), "the original could not be deleted, so it is still there");
            Assert.Contains(
                logger.Entries,
                e => e.Level == LogLevel.Warning
                    && e.Message.Contains(source, StringComparison.Ordinal)
                    && e.Message.Contains("manually", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    // Camera footage on a case-insensitive filesystem (macOS APFS, SMB/CIFS, exFAT): GOPR0001.MP4 under an
    // mp4 profile targets "GOPR0001.mp4". The names differ only by case, so the ordinal comparison called
    // them different files, File.Exists found the source itself through case folding, and the output was
    // written as "GOPR0001 (1).mp4" — the original deleted and GOPR0001.srt no longer matching anything.
    // A case-only difference is not a container change, so the source is replaced under its own name on
    // every filesystem.
    [Fact]
    public void ReplaceInPlace_ExtensionDifferingOnlyByCase_ReplacesTheSourceUnderItsOwnName()
    {
        var work = NewWorkDir();
        var temp = Path.Combine(Path.GetTempPath(), "pretranscode-oa-temp-" + Path.GetRandomFileName() + ".mp4");
        try
        {
            var source = Path.Combine(work, "GOPR0001.MP4");
            File.WriteAllText(source, "original");
            File.WriteAllText(Path.Combine(work, "GOPR0001.srt"), "subs");
            File.WriteAllText(temp, "encoded");

            var final = OutputApplier.Apply(Profile(OutputHandlingMode.ReplaceInPlace, "mp4"), source, temp);

            Assert.Equal(source, final);
            Assert.Equal("encoded", File.ReadAllText(source));
            Assert.Equal(
                new[] { "GOPR0001.MP4", "GOPR0001.srt" },
                Directory.GetFiles(work).Select(f => Path.GetFileName(f)).OrderBy(n => n, StringComparer.Ordinal));
        }
        finally
        {
            Directory.Delete(work, recursive: true);
            File.Delete(temp);
        }
    }

    // On a case-sensitive filesystem both spellings can be two different files. Only the source may be
    // touched; the other file must come through untouched. (On a case-insensitive filesystem the two
    // names are one file and the situation cannot be built, which the test detects rather than assumes.)
    [Fact]
    public void ReplaceInPlace_ExtensionDifferingOnlyByCase_NeverTouchesAnotherFileOfThatSpelling()
    {
        var work = NewWorkDir();
        var temp = Path.Combine(Path.GetTempPath(), "pretranscode-oa-temp-" + Path.GetRandomFileName() + ".mp4");
        try
        {
            var source = Path.Combine(work, "GOPR0001.MP4");
            var other = Path.Combine(work, "GOPR0001.mp4");
            File.WriteAllText(source, "original");
            var caseSensitive = !File.Exists(other);
            if (caseSensitive)
            {
                File.WriteAllText(other, "a different file");
            }

            File.WriteAllText(temp, "encoded");

            var final = OutputApplier.Apply(Profile(OutputHandlingMode.ReplaceInPlace, "mp4"), source, temp);

            Assert.Equal(source, final);
            Assert.Equal("encoded", File.ReadAllText(source));
            Assert.Equal(caseSensitive ? 2 : 1, Directory.GetFiles(work).Length);
            if (caseSensitive)
            {
                Assert.Equal("a different file", File.ReadAllText(other));
            }
        }
        finally
        {
            Directory.Delete(work, recursive: true);
            File.Delete(temp);
        }
    }

    // A restart or `docker stop` (SIGKILL after 10 s) during a multi-minute cross-volume copy left the
    // partial ".pretranscode-tmp-*" scratch next to the media for ever — startup only clears the plugin's
    // own temp directory. A scratch nobody has written to for hours is debris; one written moments ago may
    // be another job's copy in progress and must be left alone.
    [Theory]
    [InlineData(OutputHandlingMode.ReplaceInPlace)]
    [InlineData(OutputHandlingMode.AddAsAlternateVersion)]
    [InlineData(OutputHandlingMode.SeparateDirectory)]
    public void Apply_RemovesStaleScratchFromAKilledApply_ButNotOneStillBeingWritten(OutputHandlingMode mode)
    {
        var work = NewWorkDir();
        try
        {
            var source = Path.Combine(work, "movie.mkv");
            var temp = Path.Combine(work, "tmp.mp4");
            var outDir = Path.Combine(work, "out");
            var scratchDir = mode == OutputHandlingMode.SeparateDirectory ? outDir : work;
            Directory.CreateDirectory(scratchDir);
            File.WriteAllText(source, "original");
            File.WriteAllText(temp, "encoded");
            var stale = Path.Combine(scratchDir, ".pretranscode-tmp-0123456789abcdef0123456789abcdef.mkv");
            var live = Path.Combine(scratchDir, ".pretranscode-tmp-fedcba9876543210fedcba9876543210.mkv");
            File.WriteAllText(stale, "half a copy");
            File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddHours(-7));
            File.WriteAllText(live, "another job's copy in progress");
            var logger = new CapturingLogger();

            OutputApplier.Apply(Profile(mode, "mp4", mode == OutputHandlingMode.SeparateDirectory ? outDir : string.Empty), source, temp, logger);

            Assert.False(File.Exists(stale), "a scratch nobody has written to for hours is debris");
            Assert.True(File.Exists(live), "a scratch written moments ago may belong to a concurrent job");
            Assert.Contains(logger.Entries, e => e.Message.Contains(stale, StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }
}
