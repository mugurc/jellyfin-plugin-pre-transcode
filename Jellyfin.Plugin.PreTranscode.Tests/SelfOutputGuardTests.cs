using System.IO;
using Jellyfin.Plugin.PreTranscode.Configuration;
using Jellyfin.Plugin.PreTranscode.Encoding;
using Jellyfin.Plugin.PreTranscode.Library;

namespace Jellyfin.Plugin.PreTranscode.Tests;

// A "separate directory" profile with no output directory writes beside the source. If its container
// also maps to the extension the source already has, the output path IS the source path — and the
// applier's MakeUnique fallback then writes "Movie (1).mkv", which Jellyfin indexes as its own item and
// the next sweep re-encodes into "Movie (1) (1).mkv", one generation per sweep. The evaluator must
// refuse that configuration instead of building the chain.
public class SelfOutputGuardTests
{
    // Paths are built with Path.Combine so the separator matches the host running the tests; the
    // production comparison is the filesystem's own (OutputApplier.IsSameFile).
    private static readonly string MediaDir = Path.Combine(Path.GetTempPath(), "media");
    private static readonly string OutputDir = Path.Combine(Path.GetTempPath(), "output");

    private static EncodingProfile SeparateDirectory(string container, string outputDirectory = "")
    {
        return new EncodingProfile
        {
            Name = "P",
            OutputMode = OutputHandlingMode.SeparateDirectory,
            OutputDirectory = outputDirectory,
            Container = container
        };
    }

    [Fact]
    public void SameContainerBesideTheSource_IsRefused()
    {
        var mkv = Path.Combine(MediaDir, "Movie.mkv");
        Assert.True(ItemEvaluator.WritesOverItsOwnSource(SeparateDirectory("matroska"), mkv));
        Assert.True(ItemEvaluator.WritesOverItsOwnSource(SeparateDirectory("mkv"), mkv));
        Assert.True(ItemEvaluator.WritesOverItsOwnSource(SeparateDirectory("mp4"), Path.Combine(MediaDir, "Movie.mp4")));
    }

    // The source already sitting in the configured output directory is the same trap by another route.
    [Fact]
    public void SourceAlreadyInTheOutputDirectory_IsRefused()
    {
        var profile = SeparateDirectory("matroska", OutputDir);
        Assert.True(ItemEvaluator.WritesOverItsOwnSource(profile, Path.Combine(OutputDir, "Movie.mkv")));
    }

    [Fact]
    public void DifferentContainer_IsAllowed()
    {
        Assert.False(ItemEvaluator.WritesOverItsOwnSource(SeparateDirectory("mp4"), Path.Combine(MediaDir, "Movie.mkv")));
    }

    [Fact]
    public void DifferentOutputDirectory_IsAllowed()
    {
        Assert.False(ItemEvaluator.WritesOverItsOwnSource(SeparateDirectory("matroska", OutputDir), Path.Combine(MediaDir, "Movie.mkv")));
    }

    // Replace-in-place has no distinct target by design; it is the correct way to shrink a file in place
    // and must not be caught by this guard.
    [Fact]
    public void ReplaceInPlace_IsAllowed()
    {
        var profile = new EncodingProfile { Name = "P", OutputMode = OutputHandlingMode.ReplaceInPlace, Container = "matroska" };
        Assert.False(ItemEvaluator.WritesOverItsOwnSource(profile, Path.Combine(MediaDir, "Movie.mkv")));
    }

    // Found by a live run: the guard was only in the evaluator, but "Transcode a single item" puts a job
    // straight into the queue, so the executor reaches this configuration without the evaluator ever
    // seeing it — and produced the "(2).mkv" the guard exists to prevent. Both entry points must consult
    // one definition, so this asserts the evaluator delegates rather than keeping its own copy.
    [Fact]
    public void EvaluatorAndExecutorShareOneDefinition()
    {
        var trap = SeparateDirectory("matroska");
        var fine = SeparateDirectory("mp4");
        var mkv = Path.Combine(MediaDir, "Movie.mkv");

        Assert.Equal(OutputApplier.WritesOverItsOwnSource(trap, mkv), ItemEvaluator.WritesOverItsOwnSource(trap, mkv));
        Assert.Equal(OutputApplier.WritesOverItsOwnSource(fine, mkv), ItemEvaluator.WritesOverItsOwnSource(fine, mkv));
        Assert.True(OutputApplier.WritesOverItsOwnSource(trap, mkv));
        Assert.False(OutputApplier.WritesOverItsOwnSource(fine, mkv));
    }

    [Fact]
    public void AlternateVersion_IsAllowed()
    {
        var profile = new EncodingProfile
        {
            Name = "P", OutputMode = OutputHandlingMode.AddAsAlternateVersion,
            Container = "matroska", AlternateVersionLabel = "Pre-Transcode"
        };
        Assert.False(ItemEvaluator.WritesOverItsOwnSource(profile, Path.Combine(MediaDir, "Movie.mkv")));
    }
}
