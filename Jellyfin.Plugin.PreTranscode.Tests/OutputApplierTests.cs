using System.IO;
using Jellyfin.Plugin.PreTranscode.Configuration;
using Jellyfin.Plugin.PreTranscode.Encoding;

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
}
