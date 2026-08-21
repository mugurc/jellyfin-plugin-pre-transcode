using System;
using System.Collections.Generic;
using System.IO;
using Jellyfin.Plugin.PreTranscode.Jobs;
using Jellyfin.Plugin.PreTranscode.Library;

namespace Jellyfin.Plugin.PreTranscode.Tests;

public class ItemEvaluatorTests
{
    [Theory]
    [InlineData("470f8009-e889-4134-af97-fa8158de2c04", "470f8009e8894134af97fa8158de2c04")]
    [InlineData("470f8009e8894134af97fa8158de2c04", "470f8009e8894134af97fa8158de2c04")]
    [InlineData("{470F8009-E889-4134-AF97-FA8158DE2C04}", "470f8009e8894134af97fa8158de2c04")]
    [InlineData("not-a-guid", "notaguid")]
    public void NormalizeGuid_NormalizesToDashlessLowercase(string input, string expected)
    {
        Assert.Equal(expected, ItemEvaluator.NormalizeGuid(input));
    }

    [Fact]
    public void IsStable_TrueWhenFileOlderThanWindow()
    {
        WithTempFile(DateTime.UtcNow.AddMinutes(-5), path => Assert.True(ItemEvaluator.IsStable(path, 60)));
    }

    [Fact]
    public void IsStable_FalseWhenFileRecentlyWritten()
    {
        // The just-added-file case: within the stability window, so the monitor defers instead of skipping.
        WithTempFile(DateTime.UtcNow, path => Assert.False(ItemEvaluator.IsStable(path, 60)));
    }

    [Fact]
    public void IsStable_TrueWhenWindowDisabled()
    {
        WithTempFile(DateTime.UtcNow, path => Assert.True(ItemEvaluator.IsStable(path, 0)));
    }

    private static void WithTempFile(DateTime lastWriteUtc, Action<string> assert)
    {
        var path = Path.GetTempFileName();
        try
        {
            File.SetLastWriteTimeUtc(path, lastWriteUtc);
            assert(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static TranscodeJob Job(string src, string profile, JobStatus status, string output = "", long outputSize = 0) =>
        new() { SourcePath = src, ProfileId = profile, Status = status, OutputPath = output, OutputSizeBytes = outputSize };

    [Fact]
    public void AlreadyHandled_TrueWhenCompletedForSameProfileAndOutputExists()
    {
        var jobs = new[] { Job("/a.mkv", "p1", JobStatus.Completed, "/out.mkv") };
        Assert.True(ItemEvaluator.AlreadyHandled(jobs, "/a.mkv", "p1", _ => 1, 3));
    }

    [Fact]
    public void AlreadyHandled_FalseWhenCompletedOutputMissing()
    {
        var jobs = new[] { Job("/a.mkv", "p1", JobStatus.Completed, "/out.mkv") };
        Assert.False(ItemEvaluator.AlreadyHandled(jobs, "/a.mkv", "p1", _ => 0, 3));
    }

    [Fact]
    public void AlreadyHandled_TrueWhenSkippedKeptOriginalStillExists()
    {
        // DiscardOutputIfLarger records the kept original as the job's output. That source is handled and
        // must not be re-queued and re-transcoded on every sweep just to discard a larger result again.
        var jobs = new[] { Job("/a.mkv", "p1", JobStatus.Skipped, "/a.mkv") };
        Assert.True(ItemEvaluator.AlreadyHandled(jobs, "/a.mkv", "p1", _ => 1, 3));
    }

    [Fact]
    public void AlreadyHandled_FalseWhenSkippedWithoutRecordedOutput()
    {
        // An "already compliant" skip records no output path, so it must not block a future re-evaluation.
        var jobs = new[] { Job("/a.mkv", "p1", JobStatus.Skipped) };
        Assert.False(ItemEvaluator.AlreadyHandled(jobs, "/a.mkv", "p1", _ => 1, 3));
    }

    // Replace-in-place with a container change renames the file, so the finished job records the OLD
    // name and the file that now exists has no record of its own. Matching only on the source path, the
    // next sweep saw an unfamiliar file, matched the same size/codec rule again, and re-encoded the
    // previous encode — a generation lost per sweep for as long as it stayed over the threshold.
    [Fact]
    public void AlreadyHandled_TrueWhenThePathIsAPreviousRunsOutput()
    {
        var jobs = new[] { Job("/Movie.mp4", "p1", JobStatus.Completed, "/Movie.mkv") };
        Assert.True(ItemEvaluator.AlreadyHandled(jobs, "/Movie.mkv", "p1", _ => 1, 3));
    }

    [Fact]
    public void AlreadyHandled_OutputRecognitionIsCaseInsensitiveAndProfileScoped()
    {
        var jobs = new[] { Job("/Movie.mp4", "p1", JobStatus.Completed, "/Movie.mkv") };

        Assert.True(ItemEvaluator.AlreadyHandled(jobs, "/MOVIE.MKV", "p1", _ => 1, 3));

        // A different profile transcoding this file is a legitimate chain, not the loop above.
        Assert.False(ItemEvaluator.AlreadyHandled(jobs, "/Movie.mkv", "p2", _ => 1, 3));
    }

    [Fact]
    public void AlreadyHandled_UnfinishedJobsOutputDoesNotCount()
    {
        // A job that failed or is still running has not produced anything to recognise.
        var jobs = new[]
        {
            Job("/Movie.mp4", "p1", JobStatus.Failed, "/Movie.mkv"),
            Job("/Other.mp4", "p1", JobStatus.Processing, "/Movie.mkv")
        };
        Assert.False(ItemEvaluator.AlreadyHandled(jobs, "/Movie.mkv", "p1", _ => 1, 0));
    }

    [Fact]
    public void AlreadyHandled_FalseForDifferentProfileOrSource()
    {
        var jobs = new[]
        {
            Job("/a.mkv", "p2", JobStatus.Completed, "/out.mkv"), // different profile
            Job("/b.mkv", "p1", JobStatus.Completed, "/out.mkv"), // different source
        };
        Assert.False(ItemEvaluator.AlreadyHandled(jobs, "/a.mkv", "p1", _ => 1, 3));
    }

    [Fact]
    public void AlreadyHandled_TrueAfterMaxFailedAttempts()
    {
        var jobs = new[]
        {
            Job("/a.mkv", "p1", JobStatus.Failed),
            Job("/a.mkv", "p1", JobStatus.Failed),
            Job("/a.mkv", "p1", JobStatus.Failed),
        };
        Assert.True(ItemEvaluator.AlreadyHandled(jobs, "/a.mkv", "p1", _ => 0, 3));
    }

    [Fact]
    public void AlreadyHandled_FalseBelowMaxFailedAttempts()
    {
        var jobs = new[]
        {
            Job("/a.mkv", "p1", JobStatus.Failed),
            Job("/a.mkv", "p1", JobStatus.Failed),
        };
        Assert.False(ItemEvaluator.AlreadyHandled(jobs, "/a.mkv", "p1", _ => 0, 3));
    }

    // Path identity is not file identity: deleting a transcode and dropping a different file in under the
    // same name used to blacklist that path for this profile for ever, while the log blamed a source that
    // is not this file at all.
    [Fact]
    public void AlreadyHandled_FalseWhenADifferentFileNowSitsAtTheOutputPath()
    {
        var jobs = new[] { Job("/Movie.mp4", "p1", JobStatus.Completed, "/Movie.mkv", outputSize: 9_000_000_000) };

        Assert.True(ItemEvaluator.AlreadyHandled(jobs, "/Movie.mkv", "p1", _ => 9_000_000_000, 3));
        Assert.False(ItemEvaluator.AlreadyHandled(jobs, "/Movie.mkv", "p1", _ => 60_000_000_000, 3));
    }

    // A record written before the size was stored is trusted as before, so upgrading does not set a whole
    // library re-transcoding itself.
    [Fact]
    public void AlreadyHandled_RecordWithoutASize_IsStillTrusted()
    {
        var jobs = new[] { Job("/Movie.mp4", "p1", JobStatus.Completed, "/Movie.mkv") };

        Assert.True(ItemEvaluator.AlreadyHandled(jobs, "/Movie.mkv", "p1", _ => 123, 3));
    }
}
