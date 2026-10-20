using System;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.PreTranscode.Jobs;

namespace Jellyfin.Plugin.PreTranscode.Tests;

/// <summary>
/// A job reaching its final state while another thread enqueues — and so trims the history — must not
/// be dropped half-way through the change. Uses <see cref="Plugin.Instance"/> for the history cap.
/// </summary>
[Collection(PluginInstanceCollection.Name)]
public class JobQueueFinishTests
{
    [Fact]
    public async Task UpdateWithAChange_HoldsTheLock_SoAConcurrentTrimKeepsTheFinishingJob()
    {
        using var rig = new QueueRig();
        rig.Config.MaxFinishedJobsKept = 2;

        // Queued a week ago by a big sweep, so its CreatedUtc is older than anything in the history.
        var running = new TranscodeJob { SourcePath = "/m/running.mkv", ProfileId = "p1", CreatedUtc = DateTime.UtcNow.AddDays(-7) };
        Assert.True(rig.Queue.Enqueue(running));
        for (var i = 0; i < 2; i++)
        {
            Assert.True(rig.Queue.Enqueue(new TranscodeJob
            {
                SourcePath = "/m/done" + i + ".mkv",
                ProfileId = "p1",
                CreatedUtc = DateTime.UtcNow.AddDays(-1),
                Status = JobStatus.Completed,
                FinishedUtc = DateTime.UtcNow.AddHours(-1)
            }));
        }

        Assert.Same(running, rig.Queue.ClaimNextPending());

        Task<bool>? concurrent = null;
        rig.Queue.Update(running, j =>
        {
            j.Status = JobStatus.Completed;

            // Mid-change — final status set, finish time not yet — another thread enqueues and trims.
            concurrent = Task.Run(() => rig.Queue.Enqueue(new TranscodeJob { SourcePath = "/m/new.mkv", ProfileId = "p1", CreatedUtc = DateTime.UtcNow }));
            Assert.False(concurrent.Wait(200), "the enqueue ran while the job was half-changed");

            j.OutputPath = "/out/running.mkv";
            j.FinishedUtc = DateTime.UtcNow;
        });

        Assert.True(await concurrent!);
        var kept = rig.Queue.Get(running.Id);
        Assert.NotNull(kept);
        Assert.Equal(JobStatus.Completed, kept!.Status);
        Assert.Equal(2, rig.Queue.GetJobs().Count(j => JobQuery.IsFinished(j.Status)));
    }
}
