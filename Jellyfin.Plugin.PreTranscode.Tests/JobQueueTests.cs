using System.IO;
using System.Linq;
using Jellyfin.Plugin.PreTranscode.Jobs;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Jellyfin.Plugin.PreTranscode.Tests;

public class JobQueueTests
{
    private static JobQueue NewQueue()
    {
        return NewQueue(out _);
    }

    private static JobQueue NewQueue(out string dataPath)
    {
        dataPath = Path.Combine(Path.GetTempPath(), "pt-jq-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dataPath);
        return OpenQueue(dataPath);
    }

    private static JobQueue OpenQueue(string dataPath)
    {
        var paths = new Mock<IApplicationPaths>();
        paths.SetupGet(p => p.DataPath).Returns(dataPath);
        return new JobQueue(paths.Object, NullLogger<JobQueue>.Instance);
    }

    private static string QueueFile(string dataPath) => Path.Combine(dataPath, "pretranscode", "queue.json");

    private static string PersistedText(string dataPath)
    {
        var file = QueueFile(dataPath);
        return File.Exists(file) ? File.ReadAllText(file) : string.Empty;
    }

    private static TranscodeJob Job(string source, string profileId = "p1")
    {
        return new TranscodeJob { SourcePath = source, ProfileId = profileId };
    }

    [Fact]
    public void Enqueue_DuplicateActivePath_IsRejected()
    {
        var q = NewQueue();
        Assert.True(q.Enqueue(Job("/media/a.mkv")));
        Assert.False(q.Enqueue(Job("/media/a.mkv")));
        Assert.Single(q.GetJobs());
    }

    [Fact]
    public void Enqueue_RedundantPredicateTrue_IsRejectedAtomically()
    {
        // Simulates the TOCTOU fix: an earlier job for this source completed while the caller was probing,
        // so the redundancy predicate (evaluated under the lock, with the live list) rejects the add even
        // though no Pending/Processing job exists for the path.
        var q = NewQueue();
        var added = q.Enqueue(Job("/media/a.mkv"), _ => true);
        Assert.False(added);
        Assert.Empty(q.GetJobs());
    }

    [Fact]
    public void Enqueue_RedundantPredicateFalse_IsAdded()
    {
        var q = NewQueue();
        Assert.True(q.Enqueue(Job("/media/a.mkv"), _ => false));
        Assert.Single(q.GetJobs());
    }

    [Fact]
    public void Enqueue_PredicateSeesLiveList_CanRejectByCompletedSibling()
    {
        var q = NewQueue();
        var done = Job("/media/a.mkv");
        done.Status = JobStatus.Completed;
        done.OutputPath = "/media/a - H.264.mkv";
        Assert.True(q.Enqueue(done, _ => false)); // seed a completed record (not pending/processing)

        // A fresh evaluation of the same source: predicate inspects the list and finds the completed job.
        var rejected = !q.Enqueue(
            Job("/media/a.mkv"),
            jobs => System.Linq.Enumerable.Any(jobs, j => j.Status == JobStatus.Completed
                && string.Equals(j.SourcePath, "/media/a.mkv", System.StringComparison.OrdinalIgnoreCase)));
        Assert.True(rejected);
        Assert.Single(q.GetJobs());
    }

    [Fact]
    public void Requeue_ProcessingJob_IsRefused()
    {
        var q = NewQueue();
        q.Enqueue(Job("/media/a.mkv"));
        var claimed = q.ClaimNextPending();
        Assert.NotNull(claimed);
        Assert.Equal(JobStatus.Processing, claimed!.Status);

        Assert.False(q.Requeue(claimed.Id));
        Assert.Equal(JobStatus.Processing, q.Get(claimed.Id)!.Status);
    }

    [Fact]
    public void Requeue_CompletedJob_ReturnsToPending()
    {
        var q = NewQueue();
        var job = Job("/media/a.mkv");
        job.Status = JobStatus.Completed;
        q.Enqueue(job);

        Assert.True(q.Requeue(job.Id));
        Assert.Equal(JobStatus.Pending, q.Get(job.Id)!.Status);
    }

    // Persistence rewrites the whole file, so a sweep that enqueued 50,000 items used to do that 50,000
    // times over a file growing to ~33 MB — tens of minutes of I/O with the queue lock held. Enqueueing
    // is now coalesced into the periodic flush instead.
    [Fact]
    public void Enqueue_IsCoalesced_NotWrittenThroughPerItem()
    {
        var q = NewQueue(out var dataPath);

        q.Enqueue(Job("/media/a.mkv"));
        q.Enqueue(Job("/media/b.mkv"));

        Assert.DoesNotContain("/media/a.mkv", PersistedText(dataPath), System.StringComparison.Ordinal);
        Assert.Equal(2, q.GetJobs().Count);
    }

    // What a coalesced write must never do is lose the work on a clean shutdown.
    [Fact]
    public void Dispose_PersistsWhatWasCoalesced()
    {
        var q = NewQueue(out var dataPath);
        q.Enqueue(Job("/media/a.mkv"));

        q.Dispose();

        Assert.Contains("/media/a.mkv", PersistedText(dataPath), System.StringComparison.Ordinal);
        Assert.Single(OpenQueue(dataPath).GetJobs());
    }

    // Losing a Completed record costs a real re-encode — under Replace-in-place with the compliance skip
    // off, a second lossy pass over the previous encode — so a final state still writes through.
    [Fact]
    public void Update_ToAFinalState_IsWrittenThroughImmediately()
    {
        var q = NewQueue(out var dataPath);
        var job = Job("/media/a.mkv");
        q.Enqueue(job);

        job.Status = JobStatus.Completed;
        job.OutputPath = "/media/a.mp4";
        q.Update(job);

        Assert.Contains("/media/a.mp4", PersistedText(dataPath), System.StringComparison.Ordinal);
    }

    // The transient display text between those states is not worth a 33 MB write each.
    [Fact]
    public void Update_WithOnlyAStatusDetail_IsCoalesced()
    {
        var q = NewQueue(out var dataPath);
        var job = Job("/media/a.mkv");
        q.Enqueue(job);
        q.ClaimNextPending();

        job.StatusDetail = "verifying";
        q.Update(job);

        Assert.DoesNotContain("verifying", PersistedText(dataPath), System.StringComparison.Ordinal);
        Assert.Equal("verifying", q.Get(job.Id)!.StatusDetail);
    }

    // AttemptCount is what stops a file that fails every time from being retried for ever, so a claim
    // stays as durable as it was before.
    [Fact]
    public void ClaimNextPending_IsWrittenThroughImmediately()
    {
        var q = NewQueue(out var dataPath);
        q.Enqueue(Job("/media/a.mkv"));

        var claimed = q.ClaimNextPending();

        Assert.NotNull(claimed);
        Assert.Contains("/media/a.mkv", PersistedText(dataPath), System.StringComparison.Ordinal);
    }

    // Admin actions write through: one that silently un-does itself after a restart is worse than the
    // write it costs.
    [Fact]
    public void AdminActions_AreWrittenThroughImmediately()
    {
        var q = NewQueue(out var dataPath);
        var job = Job("/media/a.mkv");
        q.Enqueue(job);
        q.ClaimNextPending();
        Assert.Contains("/media/a.mkv", PersistedText(dataPath), System.StringComparison.Ordinal);

        Assert.True(q.Remove(job.Id));

        Assert.DoesNotContain("/media/a.mkv", PersistedText(dataPath), System.StringComparison.Ordinal);
    }

    // "Cancel all" used to rewrite the whole queue file once per cancelled job. One pass, one write —
    // and a running encode is left alone, because only the queue processor can abort a live ffmpeg.
    [Fact]
    public void CancelAllPending_CancelsEveryPendingAndLeavesTheRunningOne()
    {
        var q = NewQueue(out var dataPath);
        q.Enqueue(Job("/media/a.mkv"));
        q.Enqueue(Job("/media/b.mkv"));
        q.Enqueue(Job("/media/c.mkv"));
        var running = q.ClaimNextPending();
        Assert.NotNull(running);

        Assert.Equal(2, q.CancelAllPending());

        Assert.Equal(JobStatus.Processing, q.Get(running!.Id)!.Status);
        Assert.Equal(2, q.GetJobs().Count(j => j.Status == JobStatus.Cancelled));
        Assert.Contains("Cancelled", PersistedText(dataPath), System.StringComparison.Ordinal);
    }

    [Fact]
    public void CancelAllPending_WithNothingPending_DoesNothing()
    {
        var q = NewQueue();
        var job = Job("/media/a.mkv");
        job.Status = JobStatus.Completed;
        q.Enqueue(job);

        Assert.Equal(0, q.CancelAllPending());
        Assert.Equal(JobStatus.Completed, q.Get(job.Id)!.Status);
    }
}
