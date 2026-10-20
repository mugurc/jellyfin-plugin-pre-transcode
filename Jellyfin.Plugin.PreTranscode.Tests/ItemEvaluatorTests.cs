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

    // Same-container replace-in-place records OutputPath == SourcePath, and so does a "kept original"
    // (DiscardOutputIfLarger) skip. The source-path branch accepted any file at that path, so an upgrade
    // or re-rip landing under the same name was skipped for ever as "already transcoded".
    [Theory]
    [InlineData(JobStatus.Completed)] // same-container replace in place
    [InlineData(JobStatus.Skipped)] // kept the original because the transcode was not smaller
    public void AlreadyHandled_FalseWhenANewFileArrivedAtAPathWhoseOutputWasItself(JobStatus status)
    {
        var jobs = new[] { Job("/m/Movie.mkv", "p1", status, "/m/Movie.mkv", outputSize: 4_000_000_000) };

        Assert.True(ItemEvaluator.AlreadyHandled(jobs, "/m/Movie.mkv", "p1", _ => 4_000_000_000, 3));
        Assert.False(ItemEvaluator.AlreadyHandled(jobs, "/m/Movie.mkv", "p1", _ => 60_000_000_000, 3));
    }

    [Fact]
    public void AlreadyHandled_SamePathRecordWithoutASize_IsStillTrusted()
    {
        var jobs = new[] { Job("/m/Movie.mkv", "p1", JobStatus.Completed, "/m/Movie.mkv") };

        Assert.True(ItemEvaluator.AlreadyHandled(jobs, "/m/Movie.mkv", "p1", _ => 60_000_000_000, 3));
    }

    // Replace-in-place could not delete the original (a player had it open on Windows), but the new file
    // is in place and the job completed. The leftover original must count as handled, or the next sweep
    // re-encodes it into "Movie (1).mp4".
    [Fact]
    public void AlreadyHandled_TrueForAnOriginalLeftBehindByACompletedReplace()
    {
        var jobs = new[] { Job("/m/Movie.mkv", "p1", JobStatus.Completed, "/m/Movie.mp4", outputSize: 4_000_000_000) };

        Assert.True(ItemEvaluator.AlreadyHandled(jobs, "/m/Movie.mkv", "p1", path => path == "/m/Movie.mp4" ? 4_000_000_000 : 9_000_000_000, 3));
    }

    // The leftover is handled, but the admin still has to delete it — and Jellyfin indexes it as a second
    // item meanwhile — so each sweep names it and the file that replaced it.
    [Fact]
    public void OriginalLeftBehind_NamesTheReplacementOfAnUndeletedOriginal()
    {
        var jobs = new[]
        {
            Job("/m/Movie.mkv", "p1", JobStatus.Completed, "/m/Movie.mp4", outputSize: 4_000_000_000),
            Job("/m/Same.mkv", "p1", JobStatus.Completed, "/m/Same.mkv", outputSize: 4_000_000_000),
            Job("/m/Kept.mkv", "p1", JobStatus.Skipped, "/m/Kept.mkv", outputSize: 4_000_000_000),
        };

        Assert.Equal("/m/Movie.mp4", ItemEvaluator.OriginalLeftBehind(jobs, "/m/Movie.mkv", "p1", _ => 4_000_000_000));
        Assert.Null(ItemEvaluator.OriginalLeftBehind(jobs, "/m/Movie.mkv", "p2", _ => 4_000_000_000));
        Assert.Null(ItemEvaluator.OriginalLeftBehind(jobs, "/m/Movie.mkv", "p1", _ => 0)); // the replacement is gone too

        // Replaced under its own name, or kept as the better file: nothing was left behind.
        Assert.Null(ItemEvaluator.OriginalLeftBehind(jobs, "/m/Same.mkv", "p1", _ => 4_000_000_000));
        Assert.Null(ItemEvaluator.OriginalLeftBehind(jobs, "/m/Kept.mkv", "p1", _ => 4_000_000_000));
    }

    // A file written after the replacement finished is not the undeleted original but something new that
    // arrived under the old name (an upgrade), and telling the admin to delete it would be wrong.
    [Fact]
    public void OriginalLeftBehind_IgnoresAFileWrittenAfterTheReplacement()
    {
        var finished = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        var job = Job("/m/Movie.mkv", "p1", JobStatus.Completed, "/m/Movie.mp4", outputSize: 4_000_000_000);
        job.FinishedUtc = finished;
        var jobs = new[] { job };

        Assert.Equal("/m/Movie.mp4", ItemEvaluator.OriginalLeftBehind(jobs, "/m/Movie.mkv", "p1", _ => 1, finished.AddDays(-30)));
        Assert.Null(ItemEvaluator.OriginalLeftBehind(jobs, "/m/Movie.mkv", "p1", _ => 1, finished.AddDays(1)));
    }

    // Failures are counted per path, so three failures on a broken download used to lock out the fixed
    // file that later replaced it under the same name. Failures recorded before the current file was
    // written were about some other file.
    [Fact]
    public void AlreadyHandled_FailuresBeforeTheFileWasLastWritten_DoNotCount()
    {
        var failedAt = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        var jobs = new[]
        {
            FailedAt("/a.mkv", failedAt),
            FailedAt("/a.mkv", failedAt.AddHours(1)),
            FailedAt("/a.mkv", failedAt.AddHours(2)),
        };

        Assert.True(ItemEvaluator.AlreadyHandled(jobs, "/a.mkv", "p1", _ => 0, 3, sourceWrittenUtc: failedAt.AddMinutes(-5)));
        Assert.False(ItemEvaluator.AlreadyHandled(jobs, "/a.mkv", "p1", _ => 0, 3, sourceWrittenUtc: failedAt.AddDays(1)));

        // Unknown write time, or a record with no finish time: counted, exactly as before.
        Assert.True(ItemEvaluator.AlreadyHandled(jobs, "/a.mkv", "p1", _ => 0, 3, sourceWrittenUtc: null));
        var undated = new[] { Job("/a.mkv", "p1", JobStatus.Failed), Job("/a.mkv", "p1", JobStatus.Failed), Job("/a.mkv", "p1", JobStatus.Failed) };
        Assert.True(ItemEvaluator.AlreadyHandled(undated, "/a.mkv", "p1", _ => 0, 3, sourceWrittenUtc: failedAt));
    }

    // A timestamp in the future (a NAS whose clock runs ahead, a camera with a dead clock battery) would
    // make every failure look older than the file and retry a broken encode on every sweep for ever.
    [Fact]
    public void WrittenUtcOrNull_DistrustsATimestampInTheFuture()
    {
        WithTempFile(DateTime.UtcNow.AddMinutes(-5), path => Assert.NotNull(ItemEvaluator.WrittenUtcOrNull(path)));
        WithTempFile(DateTime.UtcNow.AddDays(30), path => Assert.Null(ItemEvaluator.WrittenUtcOrNull(path)));
        Assert.Null(ItemEvaluator.WrittenUtcOrNull(Path.Combine(Path.GetTempPath(), "does-not-exist-" + Path.GetRandomFileName())));
    }

    private static TranscodeJob FailedAt(string src, DateTime finishedUtc)
    {
        var job = Job(src, "p1", JobStatus.Failed);
        job.FinishedUtc = finishedUtc;
        return job;
    }
}
