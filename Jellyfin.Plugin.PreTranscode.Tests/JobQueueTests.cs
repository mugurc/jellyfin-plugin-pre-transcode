using System.IO;
using Jellyfin.Plugin.PreTranscode.Jobs;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Jellyfin.Plugin.PreTranscode.Tests;

public class JobQueueTests
{
    private static JobQueue NewQueue()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pt-jq-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        var paths = new Mock<IApplicationPaths>();
        paths.SetupGet(p => p.DataPath).Returns(dir);
        return new JobQueue(paths.Object, NullLogger<JobQueue>.Instance);
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
}
