using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.PreTranscode.Jobs;

namespace Jellyfin.Plugin.PreTranscode.Tests;

/// <summary>
/// The optional cap on retained finished jobs. The whole queue is re-serialised on every status update,
/// so an unbounded history makes each of those writes slower forever.
/// </summary>
public class JobHistoryTrimTests
{
    private static TranscodeJob Job(string id, JobStatus status, int ageMinutes)
    {
        var created = DateTime.UtcNow.AddMinutes(-ageMinutes);
        return new TranscodeJob
        {
            Id = id,
            SourcePath = "/media/" + id + ".mkv",
            Status = status,
            CreatedUtc = created,
            FinishedUtc = status is JobStatus.Pending or JobStatus.Processing ? null : created.AddMinutes(1)
        };
    }

    [Fact]
    public void ZeroMeansUnlimited()
    {
        var jobs = Enumerable.Range(0, 50).Select(i => Job("j" + i, JobStatus.Completed, i)).ToList();
        Assert.Equal(0, JobQueue.TrimFinished(jobs, 0));
        Assert.Equal(50, jobs.Count);
    }

    [Fact]
    public void DropsTheOldestFinishedJobsFirst()
    {
        var jobs = new List<TranscodeJob>
        {
            Job("oldest", JobStatus.Completed, 300),
            Job("middle", JobStatus.Failed, 200),
            Job("newest", JobStatus.Skipped, 100)
        };

        Assert.Equal(1, JobQueue.TrimFinished(jobs, 2));
        Assert.DoesNotContain(jobs, j => j.Id == "oldest");
        Assert.Contains(jobs, j => j.Id == "middle");
        Assert.Contains(jobs, j => j.Id == "newest");
    }

    // The live queue is never eligible, however old a pending job is.
    [Fact]
    public void NeverTouchesPendingOrProcessingJobs()
    {
        var jobs = new List<TranscodeJob>
        {
            Job("ancient-pending", JobStatus.Pending, 9999),
            Job("ancient-running", JobStatus.Processing, 9998),
            Job("finished-a", JobStatus.Completed, 300),
            Job("finished-b", JobStatus.Completed, 200)
        };

        Assert.Equal(1, JobQueue.TrimFinished(jobs, 1));
        Assert.Contains(jobs, j => j.Id == "ancient-pending");
        Assert.Contains(jobs, j => j.Id == "ancient-running");
        Assert.Contains(jobs, j => j.Id == "finished-b");
    }

    [Fact]
    public void UnderTheCap_NothingIsRemoved()
    {
        var jobs = new List<TranscodeJob> { Job("a", JobStatus.Completed, 10) };
        Assert.Equal(0, JobQueue.TrimFinished(jobs, 5));
        Assert.Single(jobs);
    }

    // A record written before FinishedUtc existed must still order deterministically.
    [Fact]
    public void FallsBackToCreatedUtcWhenFinishedUtcIsMissing()
    {
        var older = Job("older", JobStatus.Completed, 500);
        older.FinishedUtc = null;
        var newer = Job("newer", JobStatus.Completed, 10);
        newer.FinishedUtc = null;

        var jobs = new List<TranscodeJob> { newer, older };
        Assert.Equal(1, JobQueue.TrimFinished(jobs, 1));
        Assert.Contains(jobs, j => j.Id == "newer");
    }
}
