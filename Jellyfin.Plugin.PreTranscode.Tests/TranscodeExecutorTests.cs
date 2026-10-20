using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PreTranscode.Configuration;
using Jellyfin.Plugin.PreTranscode.Jobs;
using Jellyfin.Plugin.PreTranscode.Library;
using Jellyfin.Plugin.PreTranscode.Media;

namespace Jellyfin.Plugin.PreTranscode.Tests;

/// <summary>
/// Drives the real <see cref="TranscodeExecutor"/> through the outcomes that decide what happens to a
/// library file. The ones that need an encode use the real ffmpeg and skip themselves without one.
/// </summary>
[Collection(PluginInstanceCollection.Name)]
[Trait("Category", "Integration")]
public class TranscodeExecutorTests
{
    // An *arr importing an upgrade under the same name while the old release is being encoded. Whatever
    // the container, the upgrade must survive and the encode of the old release must go nowhere.
    [Theory]
    [InlineData("mkv")]
    [InlineData("mp4")]
    public async Task ReplaceInPlace_SourceReplacedDuringEncode_DiscardsOutputAndKeepsTheUpgrade(string container)
    {
        var ffmpeg = FfmpegTestBinaries.Find("ffmpeg");
        var ffprobe = FfmpegTestBinaries.Find("ffprobe");
        if (ffmpeg is null || ffprobe is null)
        {
            return;
        }

        using var rig = new QueueRig(ffmpeg, ffprobe);
        rig.Config.Profiles = new() { QueueRig.Profile("p1", OutputHandlingMode.ReplaceInPlace, container) };
        var source = Path.Combine(rig.MediaDir, "Movie.mkv");
        var upgrade = Path.Combine(rig.Root, "upgrade.mkv");
        await rig.MakeClipAsync(source, 2);
        await rig.MakeClipAsync(upgrade, 2, pattern: "smptebars");
        var upgradeBytes = File.ReadAllBytes(upgrade);

        var job = rig.Claim(source);
        rig.Hooked.AfterUpdate = j =>
        {
            // The encode has finished; the import lands before the result is applied.
            if (j.StatusDetail == "verifying" && File.Exists(upgrade))
            {
                File.Move(upgrade, source, overwrite: true);
            }
        };

        await rig.Executor.ExecuteAsync(job, CancellationToken.None);

        Assert.Equal(JobStatus.Skipped, job.Status);
        Assert.Contains("source changed during the encode", job.StatusDetail, StringComparison.Ordinal);
        Assert.Equal(upgradeBytes, File.ReadAllBytes(source));
        Assert.False(File.Exists(Path.Combine(rig.MediaDir, "Movie.mp4")), "an encode of the old release was written beside the upgrade");
        Assert.Empty(Directory.EnumerateFiles(rig.TempDir));

        // Nothing about the new file was decided: the next sweep must evaluate it like any other file.
        Assert.False(ItemEvaluator.AlreadyHandled(rig.Queue.GetJobs(), source, "p1", TranscodeExecutor.FileSizeOrZero, 3));
    }

    [Fact]
    public async Task ReplaceInPlace_SourceDeletedDuringEncode_IsNotResurrected()
    {
        var ffmpeg = FfmpegTestBinaries.Find("ffmpeg");
        var ffprobe = FfmpegTestBinaries.Find("ffprobe");
        if (ffmpeg is null || ffprobe is null)
        {
            return;
        }

        using var rig = new QueueRig(ffmpeg, ffprobe);
        rig.Config.Profiles = new() { QueueRig.Profile("p1", OutputHandlingMode.ReplaceInPlace, "mkv") };
        var source = Path.Combine(rig.MediaDir, "Movie.mkv");
        await rig.MakeClipAsync(source, 2);

        var job = rig.Claim(source);
        rig.Hooked.AfterUpdate = j =>
        {
            if (j.StatusDetail == "verifying")
            {
                File.Delete(source);
            }
        };

        await rig.Executor.ExecuteAsync(job, CancellationToken.None);

        Assert.Equal(JobStatus.Skipped, job.Status);
        Assert.Contains("deleted", job.StatusDetail, StringComparison.Ordinal);
        Assert.False(File.Exists(source), "a deleted item was brought back by the encode");
        Assert.Empty(Directory.EnumerateFiles(rig.MediaDir));
        Assert.Empty(Directory.EnumerateFiles(rig.TempDir));
    }

    // A separate-directory job must never end up running under a replace-in-place default because the
    // profile it was queued with has since been deleted.
    [Fact]
    public async Task DeletedProfile_FailsTheJobInsteadOfRunningUnderTheDefault()
    {
        using var rig = new QueueRig();
        rig.Config.Profiles = new() { QueueRig.Profile("replace", OutputHandlingMode.ReplaceInPlace, "mkv") };
        rig.Config.DefaultProfileId = "replace";
        var source = Path.Combine(rig.MediaDir, "Movie.mkv");
        File.WriteAllText(source, "original");

        var job = rig.Claim(source, "deleted-separate-directory-profile");
        await rig.Executor.ExecuteAsync(job, CancellationToken.None);

        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Contains("no longer exists", job.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(0, rig.Prober.Calls);
        Assert.Equal("original", File.ReadAllText(source));
    }

    [Fact]
    public void ResolveProfile_OnlyAnEmptyProfileIdFallsBackToTheDefault()
    {
        var config = new PluginConfiguration
        {
            Profiles = new()
            {
                QueueRig.Profile("first", OutputHandlingMode.SeparateDirectory, "mkv"),
                QueueRig.Profile("default", OutputHandlingMode.ReplaceInPlace, "mkv")
            },
            DefaultProfileId = "default"
        };

        Assert.Equal("first", TranscodeExecutor.ResolveProfile(config, "first")!.Id);
        Assert.Equal("default", TranscodeExecutor.ResolveProfile(config, string.Empty)!.Id);
        Assert.Null(TranscodeExecutor.ResolveProfile(config, "deleted"));

        config.DefaultProfileId = "also-deleted";
        Assert.Equal("first", TranscodeExecutor.ResolveProfile(config, string.Empty)!.Id);
    }

    // Someone killed ffmpeg and the server kept running: that is a real failure, recorded once the
    // grace period for a shutdown has passed — not a restart to requeue, which would loop for ever on
    // an encode that is always killed.
    [Fact]
    public async Task FfmpegSignalledWhileTheServerKeepsRunning_FailsAfterTheGracePeriod()
    {
        var ffmpeg = FfmpegTestBinaries.Find("ffmpeg");
        var ffprobe = FfmpegTestBinaries.Find("ffprobe");
        if (ffmpeg is null || ffprobe is null || OperatingSystem.IsWindows())
        {
            return;
        }

        using var rig = new QueueRig(ffmpeg, ffprobe);
        rig.Config.Profiles = new() { QueueRig.Profile("p1", OutputHandlingMode.SeparateDirectory, "mkv", preset: "medium") };
        rig.Config.Profiles[0].OutputDirectory = rig.OutDir;
        rig.Executor.InterruptionGrace = TimeSpan.FromMilliseconds(300);
        var source = Path.Combine(rig.MediaDir, "Movie.mp4");
        await rig.MakeClipAsync(source, 60);

        var job = rig.Claim(source);
        var started = new TaskCompletionSource<int>();
        var run = rig.Executor.ExecuteAsync(job, CancellationToken.None, p => started.TrySetResult(p.Id));
        var pid = await started.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(await QueueRig.WaitForAsync(() => job.Progress > 0, 30000), "ffmpeg never started encoding");
        QueueRig.Signal(pid, 15);
        await run.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Contains("signal 15", job.ErrorMessage, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(rig.TempDir));
    }

    // A job finishing while another thread enqueues (and so trims the history) must never be seen with a
    // final status but no finish time: that is the window in which it was dropped as the oldest record.
    [Fact]
    public async Task FinalStates_AreNeverWrittenOutsideTheQueueLock()
    {
        using var rig = new QueueRig();
        rig.Config.Profiles = new() { QueueRig.Profile("p1", OutputHandlingMode.SeparateDirectory, "mkv") };
        rig.Config.Profiles[0].OutputDirectory = rig.OutDir;

        // Failed: the source is gone.
        var failed = rig.Claim(Path.Combine(rig.MediaDir, "missing.mkv"));
        await rig.Executor.ExecuteAsync(failed, CancellationToken.None);

        // Skipped: the output is already there.
        var present = Path.Combine(rig.MediaDir, "present.mkv");
        File.WriteAllText(present, "x");
        File.WriteAllText(Path.Combine(rig.OutDir, "present.mkv"), "done");
        var skipped = rig.Claim(present);
        await rig.Executor.ExecuteAsync(skipped, CancellationToken.None);

        // Cancelled: the probe is interrupted.
        var probed = Path.Combine(rig.MediaDir, "probed.mkv");
        File.WriteAllText(probed, "x");
        rig.Prober.Script = (_, _, token) => Task.FromCanceled<MediaProbeInfo?>(new CancellationToken(true));
        var cancelled = rig.Claim(probed);
        await rig.Executor.ExecuteAsync(cancelled, CancellationToken.None);

        Assert.Equal(JobStatus.Failed, failed.Status);
        Assert.Equal(JobStatus.Skipped, skipped.Status);
        Assert.Equal(JobStatus.Cancelled, cancelled.Status);
        Assert.All(new[] { failed, skipped, cancelled }, j => Assert.NotNull(j.FinishedUtc));
        Assert.Equal(0, rig.Hooked.PlainTerminalUpdates);
    }

    [Fact]
    public async Task CompletedJob_IsRecordedUnderTheQueueLock()
    {
        var ffmpeg = FfmpegTestBinaries.Find("ffmpeg");
        var ffprobe = FfmpegTestBinaries.Find("ffprobe");
        if (ffmpeg is null || ffprobe is null)
        {
            return;
        }

        using var rig = new QueueRig(ffmpeg, ffprobe);
        rig.Config.Profiles[0].OutputDirectory = rig.OutDir;
        var source = Path.Combine(rig.MediaDir, "Movie.mp4");
        await rig.MakeClipAsync(source, 2);

        var job = rig.Claim(source);
        await rig.Executor.ExecuteAsync(job, CancellationToken.None);

        Assert.Equal(JobStatus.Completed, job.Status);
        Assert.Equal(Path.Combine(rig.OutDir, "Movie.mkv"), job.OutputPath);
        Assert.Equal(new FileInfo(job.OutputPath).Length, job.OutputSizeBytes);
        Assert.NotNull(job.FinishedUtc);
        Assert.Equal(0, rig.Hooked.PlainTerminalUpdates);
    }
}
