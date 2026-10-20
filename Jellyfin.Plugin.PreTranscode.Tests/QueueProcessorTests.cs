using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PreTranscode.Configuration;
using Jellyfin.Plugin.PreTranscode.Ffmpeg;
using Jellyfin.Plugin.PreTranscode.Jobs;
using Jellyfin.Plugin.PreTranscode.Media;

namespace Jellyfin.Plugin.PreTranscode.Tests;

/// <summary>
/// Runs the real <see cref="QueueProcessor"/> loop over the real queue and executor, forcing the
/// interleavings that used to lose track of a job.
/// </summary>
[Collection(PluginInstanceCollection.Name)]
[Trait("Category", "Integration")]
public class QueueProcessorTests
{
    private const int SigTerm = 15;

    private static async Task StopAsync(QueueProcessor processor)
    {
        // Bounded: a run the processor has lost track of would otherwise hold the drain for its full 30 s.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await processor.StopAsync(timeout.Token);
    }

    // The admin requeues a job the instant it fails, and the loop re-claims it before the failed run's
    // worker has finished cleaning up. That cleanup must leave the new run alone.
    [Fact]
    public async Task RequeueAndReclaimBeforeTheOldWorkerCleansUp_NewRunStaysCancellable()
    {
        using var rig = new QueueRig();
        rig.Config.MaxConcurrentJobs = 2;
        var source = Path.Combine(rig.MediaDir, "Movie.mkv");
        File.WriteAllText(source, "x");

        // First run: the probe fails, so the job fails at once. Second run: the probe "takes forever"
        // and only ends when the run's token is cancelled.
        static async Task<MediaProbeInfo?> UntilCancelled(CancellationToken token)
        {
            await Task.Delay(Timeout.Infinite, token);
            return null;
        }

        rig.Prober.Script = (call, _, token) => call == 0 ? Task.FromResult<MediaProbeInfo?>(null) : UntilCancelled(token);

        var job = rig.Enqueue(source);
        var reclaimed = new TaskCompletionSource();
        var fired = 0;
        rig.Hooked.AfterUpdate = j =>
        {
            if (j.Status == JobStatus.Failed && Interlocked.Exchange(ref fired, 1) == 0)
            {
                // Still inside the failed run, before its worker's cleanup: requeue, and hold here until
                // the loop has claimed the job again and the new run is under way.
                Assert.True(rig.Queue.Requeue(j.Id));
                var deadline = DateTime.UtcNow.AddSeconds(15);
                while (rig.Prober.Calls < 2 && DateTime.UtcNow < deadline)
                {
                    Thread.Sleep(10);
                }

                reclaimed.TrySetResult();
            }
        };

        var processor = rig.CreateProcessor();
        await processor.StartAsync(CancellationToken.None);
        try
        {
            await reclaimed.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(2, rig.Prober.Calls);

            // Let the failed run's worker finish its cleanup.
            await Task.Delay(300);

            Assert.True(processor.CancelJob(job.Id));
            Assert.True(
                await QueueRig.WaitForAsync(() => rig.Queue.Get(job.Id)!.Status == JobStatus.Cancelled, 5000),
                "cancelling the re-claimed run did nothing; status " + rig.Queue.Get(job.Id)!.Status);
        }
        finally
        {
            await StopAsync(processor);
            processor.Dispose();
        }
    }

    // Same race, with the new run's ffmpeg paused at the moment the old worker cleans up: the old worker
    // must not forget the new run's process, or Resume can never wake it.
    [Fact]
    public async Task RequeueAndReclaimBeforeTheOldWorkerCleansUp_PausedNewRunStillResumes()
    {
        var ffmpeg = FfmpegTestBinaries.Find("ffmpeg");
        var ffprobe = FfmpegTestBinaries.Find("ffprobe");
        if (ffmpeg is null || ffprobe is null || OperatingSystem.IsWindows())
        {
            return;
        }

        using var rig = new QueueRig(ffmpeg, ffprobe);
        rig.Config.MaxConcurrentJobs = 2;
        rig.Config.Profiles = new() { QueueRig.Profile("p1", OutputHandlingMode.SeparateDirectory, "mkv", preset: "medium") };
        rig.Config.Profiles[0].OutputDirectory = rig.OutDir;
        var source = Path.Combine(rig.MediaDir, "Movie.mp4");
        await rig.MakeClipAsync(source, 120);
        rig.Prober.Script = (call, _, _) => call == 0 ? Task.FromResult<MediaProbeInfo?>(null) : null;

        var processor = rig.CreateProcessor();
        var job = rig.Enqueue(source);
        var paused = new TaskCompletionSource();
        var pids = Array.Empty<int>();
        var fired = 0;
        rig.Hooked.AfterUpdate = j =>
        {
            if (j.Status == JobStatus.Failed && Interlocked.Exchange(ref fired, 1) == 0)
            {
                Assert.True(rig.Queue.Requeue(j.Id));
                var deadline = DateTime.UtcNow.AddSeconds(30);
                while (!(j.Status == JobStatus.Processing && j.Progress > 0) && DateTime.UtcNow < deadline)
                {
                    Thread.Sleep(10);
                }

                // Found before the pause: on macOS, starting a child process (pgrep) while another child
                // is stopped can hang, and the cleanup below must be able to kill a frozen ffmpeg.
                pids = QueueRig.FfmpegPidsFor(j.Id);

                // The new run's ffmpeg is encoding; freeze it before the old worker cleans up.
                processor.Pause();
                paused.TrySetResult();
            }
        };

        await processor.StartAsync(CancellationToken.None);
        try
        {
            await paused.Task.WaitAsync(TimeSpan.FromSeconds(40));
            Assert.True(job.Progress > 0, "the re-claimed run never started encoding");
            await Task.Delay(300);

            processor.Resume();
            var before = job.Progress;
            Assert.True(
                await QueueRig.WaitForAsync(() => job.Progress > before, 5000),
                "the re-claimed encode stayed frozen after Resume");

            Assert.True(processor.CancelJob(job.Id));
            Assert.True(
                await QueueRig.WaitForAsync(() => rig.Queue.Get(job.Id)!.Status == JobStatus.Cancelled, 10000),
                "cancelling the re-claimed run did nothing");
        }
        finally
        {
            // Only a run the processor lost track of is still going; anything else has been reaped and its
            // pid may already belong to some other process.
            if (rig.Queue.Get(job.Id)!.Status == JobStatus.Processing)
            {
                foreach (var pid in pids)
                {
                    QueueRig.Signal(pid, 9);
                }
            }

            await StopAsync(processor);
            processor.Dispose();
        }
    }

    // systemd's default KillMode signals every process in the service, and Ctrl+C signals the process
    // group, so ffmpeg is told to stop before the host gets round to stopping the queue. That is a
    // restart, not a failed encode: the job must come back Pending, with no failure recorded.
    [Fact]
    public async Task FfmpegSignalledJustBeforeTheServerStops_JobIsRequeuedNotFailed()
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
        var source = Path.Combine(rig.MediaDir, "Movie.mp4");
        await rig.MakeClipAsync(source, 60);

        var processor = rig.CreateProcessor();
        var job = rig.Enqueue(source);
        await processor.StartAsync(CancellationToken.None);
        try
        {
            // Wait until ffmpeg is in its encode loop, so its own signal handler is installed.
            Assert.True(await QueueRig.WaitForAsync(() => job.Progress > 0, 30000), "ffmpeg never started encoding");
            foreach (var pid in QueueRig.FfmpegPidsFor(job.Id))
            {
                QueueRig.Signal(pid, SigTerm);
            }

            Assert.True(
                await QueueRig.WaitForAsync(() => job.Status != JobStatus.Processing || job.StatusDetail.Contains("signal", StringComparison.Ordinal), 10000),
                "the signalled encode was never noticed");

            // The host reaches the plugin's StopAsync a little after the signal.
            await Task.Delay(200);
            await StopAsync(processor);

            Assert.Equal(JobStatus.Pending, rig.Queue.Get(job.Id)!.Status);
            Assert.Empty(rig.Queue.Get(job.Id)!.ErrorMessage);
            Assert.Empty(Directory.EnumerateFiles(rig.TempDir));
        }
        finally
        {
            processor.Dispose();
            QueueRig.KillLeftovers(job.Id);
        }
    }

    // The same restart landing while the finished encode is being verified: the verification ffprobe dies
    // of the signal, which the prober can only report as "not media".
    [Fact]
    public async Task VerificationProbeSignalledJustBeforeTheServerStops_JobIsRequeuedNotFailed()
    {
        var ffmpeg = FfmpegTestBinaries.Find("ffmpeg");
        var ffprobe = FfmpegTestBinaries.Find("ffprobe");
        if (ffmpeg is null || ffprobe is null || OperatingSystem.IsWindows())
        {
            return;
        }

        using var rig = new QueueRig(ffmpeg, ffprobe);
        var source = Path.Combine(rig.MediaDir, "Movie.mp4");
        await rig.MakeClipAsync(source, 2);

        // The probe of the encoded output is an ffprobe that is SIGTERMed; everything else probes for real.
        static async Task<MediaProbeInfo?> KilledProbe(CancellationToken token)
        {
            await ProcessRunner.RunAsync("/bin/sh", new[] { "-c", "kill -TERM $$" }, 10000, token);
            return null;
        }

        rig.Prober.Script = (_, path, token) => path.StartsWith(rig.TempDir, StringComparison.Ordinal) ? KilledProbe(token) : null;

        var processor = rig.CreateProcessor();
        var job = rig.Enqueue(source);
        await processor.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(
                await QueueRig.WaitForAsync(() => job.StatusDetail.Contains("ffprobe was stopped by signal 15", StringComparison.Ordinal), 30000),
                "the signalled verification was never noticed; status " + job.Status + " " + job.ErrorMessage);

            await StopAsync(processor);

            Assert.Equal(JobStatus.Pending, rig.Queue.Get(job.Id)!.Status);
            Assert.Empty(rig.Queue.Get(job.Id)!.ErrorMessage);
            Assert.Empty(Directory.EnumerateFiles(rig.TempDir));
        }
        finally
        {
            processor.Dispose();
        }
    }

    // On a restart, Jellyfin only tells the plugin where its ffmpeg is a few seconds after the queue has
    // started. A job left over from before the restart must not be claimed — and probed with whatever
    // "ffmpeg" is on PATH — in that gap.
    [Fact]
    public async Task Loop_DoesNotClaimBeforeJellyfinHasSetItsFfmpegPath()
    {
        using var rig = new QueueRig();
        rig.EncoderPath = string.Empty;
        var source = Path.Combine(rig.MediaDir, "Movie.mkv");
        File.WriteAllText(source, "x");
        rig.Prober.Script = (_, _, _) => Task.FromResult<MediaProbeInfo?>(null);
        var job = rig.Enqueue(source);

        var processor = rig.CreateProcessor();
        await processor.StartAsync(CancellationToken.None);
        try
        {
            await Task.Delay(2500);
            Assert.Equal(JobStatus.Pending, job.Status);
            Assert.Equal(0, job.AttemptCount);

            rig.EncoderPath = "/usr/lib/jellyfin-ffmpeg/ffmpeg";
            Assert.True(await QueueRig.WaitForAsync(() => job.AttemptCount == 1, 6000), "the job was never claimed once the path was known");
        }
        finally
        {
            await StopAsync(processor);
            processor.Dispose();
        }
    }

    // A server that never reports a path (no ffmpeg found) still gets the PATH fallback once the wait runs
    // out, and says so once rather than on every claim.
    [Fact]
    public async Task Loop_ProceedsWithThePathFallbackOnceTheWaitRunsOut()
    {
        using var rig = new QueueRig();
        rig.EncoderPath = string.Empty;
        var source = Path.Combine(rig.MediaDir, "Movie.mkv");
        File.WriteAllText(source, "x");
        rig.Prober.Script = (_, _, _) => Task.FromResult<MediaProbeInfo?>(null);
        var first = rig.Enqueue(source);
        var second = rig.Enqueue(Path.Combine(rig.MediaDir, "Other.mkv"));
        File.WriteAllText(second.SourcePath, "x");

        var log = new ListLogger<QueueProcessor>();
        var processor = rig.CreateProcessor(log);
        processor.FfmpegPathTimeout = TimeSpan.FromMilliseconds(300);
        await processor.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(await QueueRig.WaitForAsync(() => first.AttemptCount == 1 && second.AttemptCount == 1, 15000), "the jobs were never claimed");
            Assert.Single(log.Messages, m => m.Contains("has not reported its ffmpeg path", StringComparison.Ordinal));
        }
        finally
        {
            await StopAsync(processor);
            processor.Dispose();
        }
    }

    // Pause and Resume racing (two tabs, a double click) must leave the saved flag equal to the live
    // one, or the queue comes back paused — or running — after the next restart. A guard rather than a
    // reproduction: the old window was the few instructions between releasing the lock and writing the
    // config, with nothing in it a test can hook, so hammering it rarely lands inside.
    [Fact]
    public void PauseAndResumeRacing_SavedStateAlwaysMatchesTheLiveState()
    {
        using var rig = new QueueRig();
        var processor = rig.CreateProcessor();
        var mismatches = 0;
        for (var round = 0; round < 3000; round++)
        {
            using var start = new Barrier(2);
            var pause = new Thread(() =>
            {
                start.SignalAndWait();
                processor.Pause();
            });
            var resume = new Thread(() =>
            {
                start.SignalAndWait();
                processor.Resume();
            });
            pause.Start();
            resume.Start();
            pause.Join();
            resume.Join();

            if (rig.Config.QueuePaused != rig.Queue.IsPaused)
            {
                mismatches++;
            }
        }

        processor.Dispose();
        Assert.Equal(0, mismatches);
    }
}
