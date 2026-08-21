using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.PreTranscode.Jobs;

namespace Jellyfin.Plugin.PreTranscode.Tests;

/// <summary>
/// How the pages read the job list. A library big enough to fill the queue with tens of thousands of
/// jobs made the old "fetch everything every two seconds and render a row per job" page unusable
/// (issue #6), so every view now asks for one ordered, filtered page at a time.
/// </summary>
public class JobQueryTests
{
    private static readonly DateTime Base = new(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);

    private static TranscodeJob Job(string name, JobStatus status, int createdOffset, int? finishedOffset = null, string path = "")
    {
        return new TranscodeJob
        {
            Id = name,
            DisplayName = name,
            SourcePath = string.IsNullOrEmpty(path) ? "/media/" + name + ".mkv" : path,
            Status = status,
            CreatedUtc = Base.AddMinutes(createdOffset),
            StartedUtc = status == JobStatus.Pending ? null : Base.AddMinutes(createdOffset + 1),
            FinishedUtc = finishedOffset.HasValue ? Base.AddMinutes(finishedOffset.Value) : null
        };
    }

    private static List<TranscodeJob> Sample()
    {
        return new List<TranscodeJob>
        {
            Job("done-old", JobStatus.Completed, 0, 10),
            Job("pending-b", JobStatus.Pending, 5),
            Job("failed-new", JobStatus.Failed, 2, 30),
            Job("running", JobStatus.Processing, 6),
            Job("pending-a", JobStatus.Pending, 1),
            Job("skipped", JobStatus.Skipped, 3, 20)
        };
    }

    private static string[] Names(IEnumerable<TranscodeJob> jobs) => jobs.Select(j => j.Id).ToArray();

    // The live queue reads as a running order: what is encoding, then what runs next. Oldest-pending-first
    // is the order ClaimNextPending really picks them in, so the page cannot promise a different one.
    [Fact]
    public void ActiveFilter_ShowsTheRunningOrder()
    {
        var (items, total) = JobQuery.Page(Sample(), JobFilter.Active, null, 0, 50);

        Assert.Equal(new[] { "running", "pending-a", "pending-b" }, Names(items));
        Assert.Equal(3, total);
    }

    // History reads as a log: newest first.
    [Fact]
    public void FinishedFilter_ShowsNewestFirst()
    {
        var (items, total) = JobQuery.Page(Sample(), JobFilter.Finished, null, 0, 50);

        Assert.Equal(new[] { "failed-new", "skipped", "done-old" }, Names(items));
        Assert.Equal(3, total);
    }

    // "All" is both, in that order, so the top of the list is always the part still moving.
    [Fact]
    public void AllFilter_PutsTheLiveQueueAboveHistory()
    {
        var (items, _) = JobQuery.Page(Sample(), JobFilter.All, null, 0, 50);

        Assert.Equal(
            new[] { "running", "pending-a", "pending-b", "failed-new", "skipped", "done-old" },
            Names(items));
    }

    [Fact]
    public void Paging_ReturnsTheSliceAndTheFullCount()
    {
        var (items, total) = JobQuery.Page(Sample(), JobFilter.All, null, 2, 2);

        Assert.Equal(new[] { "pending-b", "failed-new" }, Names(items));
        Assert.Equal(6, total);
    }

    // A page whose jobs were trimmed or removed underneath it must come back empty rather than silently
    // jumping the reader to a clamped last page showing different rows.
    [Fact]
    public void StartIndexPastTheEnd_IsEmptyNotClamped()
    {
        var (items, total) = JobQuery.Page(Sample(), JobFilter.All, null, 99, 50);

        Assert.Empty(items);
        Assert.Equal(6, total);
    }

    [Fact]
    public void LimitIsClampedToSomethingSane()
    {
        Assert.Single(JobQuery.Page(Sample(), JobFilter.All, null, 0, 0).Items);
        Assert.Equal(6, JobQuery.Page(Sample(), JobFilter.All, null, 0, 100000).Items.Count);
        Assert.Empty(JobQuery.Page(Sample(), JobFilter.All, null, -5, 50).Items.Skip(6));
    }

    // An admin chasing one bad encode has the filename; one checking a whole show has the title.
    [Fact]
    public void Search_MatchesNameAndPath_CaseInsensitively()
    {
        Assert.Equal(new[] { "pending-a" }, Names(JobQuery.Page(Sample(), JobFilter.All, "PENDING-A", 0, 50).Items));

        var jobs = new List<TranscodeJob> { Job("x", JobStatus.Pending, 0, path: "/media/Show/S01E02.mkv") };
        Assert.Single(JobQuery.Page(jobs, JobFilter.All, "s01e02", 0, 50).Items);
        Assert.Empty(JobQuery.Page(jobs, JobFilter.All, "nothing", 0, 50).Items);
    }

    [Fact]
    public void Search_IsIgnoredWhenBlank()
    {
        Assert.Equal(6, JobQuery.Page(Sample(), JobFilter.All, "   ", 0, 50).TotalRecordCount);
    }

    // Every running job is listed, not the first N: with a concurrency of 2 or more, hiding one would
    // leave the control center claiming less work is happening than really is.
    [Fact]
    public void Overview_ListsAllRunningAndPreviewsTheRest()
    {
        var jobs = Sample();
        jobs.Add(Job("running-2", JobStatus.Processing, 7));

        var (counts, processing, upNext, recent) = JobQuery.Overview(jobs, 1, 2);

        Assert.Equal(new[] { "running", "running-2" }, Names(processing));
        Assert.Equal(new[] { "pending-a" }, Names(upNext));
        Assert.Equal(new[] { "failed-new", "skipped" }, Names(recent));

        // The counts come from the same walk as the slices, so they cannot contradict them. Counting
        // separately let an encode finish in between and the page drew "Processing: 1" above
        // "Idle — nothing is encoding right now".
        Assert.Equal(processing.Count, counts.Processing);
    }

    [Fact]
    public void Counts_AreCountedOnce()
    {
        var counts = JobQuery.Count(Sample());

        Assert.Equal(2, counts.Pending);
        Assert.Equal(1, counts.Processing);
        Assert.Equal(1, counts.Completed);
        Assert.Equal(1, counts.Failed);
        Assert.Equal(0, counts.Cancelled);
        Assert.Equal(1, counts.Skipped);
        Assert.Equal(6, counts.Total);
    }

    // The pages each call their own view by a different word; every one of them has to land on the
    // right slice, and anything unrecognised has to fall back to showing everything rather than nothing.
    [Fact]
    public void FilterNamesFromThePagesAreAccepted()
    {
        Assert.Equal(JobFilter.Active, JobQuery.ParseFilter("active"));
        Assert.Equal(JobFilter.Active, JobQuery.ParseFilter("QUEUE"));
        Assert.Equal(JobFilter.Finished, JobQuery.ParseFilter(" finished "));
        Assert.Equal(JobFilter.Finished, JobQuery.ParseFilter("history"));
        Assert.Equal(JobFilter.All, JobQuery.ParseFilter(string.Empty));
        Assert.Equal(JobFilter.All, JobQuery.ParseFilter(null));
        Assert.Equal(JobFilter.All, JobQuery.ParseFilter("nonsense"));
    }

    // "Cancel all" stamps one instant into every pending job at once. Without a tiebreak a stable sort
    // resolved that block to insertion order — the OLDEST cancelled job first, on a newest-first tab.
    [Fact]
    public void History_TiesOnFinishedTime_FallBackToNewestQueuedFirst()
    {
        var sameInstant = 500;
        var jobs = new List<TranscodeJob>
        {
            Job("queued-first", JobStatus.Cancelled, 1, sameInstant),
            Job("queued-second", JobStatus.Cancelled, 2, sameInstant),
            Job("queued-third", JobStatus.Cancelled, 3, sameInstant)
        };

        var (items, _) = JobQuery.Page(jobs, JobFilter.Finished, null, 0, 50);

        Assert.Equal(new[] { "queued-third", "queued-second", "queued-first" }, Names(items));
    }

    // Each job's status is read once, when the query starts. The queue hands out the live job objects and
    // the executor mutates them in place, so a status re-read in a later pass could put one job in two
    // buckets at once (listed twice, total one too high) or in none (never shown on any page). The
    // enumeration below flips a job the moment the query reads it, which is the closest a test can get to
    // that interleaving.
    [Fact]
    public void JobStatusIsReadOncePerQuery()
    {
        var running = Job("running", JobStatus.Processing, 0);
        var jobs = new FlipsWhileEnumerated(new List<TranscodeJob> { running, Job("pending", JobStatus.Pending, 1) }, running);

        var (items, total) = JobQuery.Page(jobs, JobFilter.All, null, 0, 50);

        // Read once: it stays in the running bucket for this query, exactly once, and the total matches.
        Assert.Equal(new[] { "running", "pending" }, Names(items));
        Assert.Equal(2, total);
        Assert.Equal(JobStatus.Completed, running.Status);
    }

    // Flips the target job to Completed the instant the query has read it, mimicking an encode finishing
    // underneath a request.
    private sealed class FlipsWhileEnumerated : IReadOnlyList<TranscodeJob>
    {
        private readonly IReadOnlyList<TranscodeJob> _jobs;
        private readonly TranscodeJob _target;

        public FlipsWhileEnumerated(IReadOnlyList<TranscodeJob> jobs, TranscodeJob target)
        {
            _jobs = jobs;
            _target = target;
        }

        public int Count => _jobs.Count;

        public TranscodeJob this[int index] => _jobs[index];

        public IEnumerator<TranscodeJob> GetEnumerator()
        {
            foreach (var job in _jobs)
            {
                yield return job;
                if (ReferenceEquals(job, _target))
                {
                    job.Status = JobStatus.Completed;
                    job.FinishedUtc = job.CreatedUtc.AddMinutes(1);
                }
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
