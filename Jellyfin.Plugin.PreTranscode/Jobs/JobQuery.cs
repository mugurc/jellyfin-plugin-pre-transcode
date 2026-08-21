using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.PreTranscode.Jobs;

/// <summary>
/// Which slice of the queue a view is asking for.
/// </summary>
internal enum JobFilter
{
    /// <summary>Everything: the live queue first, then finished work.</summary>
    All,

    /// <summary>The live queue only — processing, then pending in the order they will actually run.</summary>
    Active,

    /// <summary>Finished work only — completed, failed, cancelled, skipped — most recent first.</summary>
    Finished
}

/// <summary>
/// Reads the job list the way the pages want it: filtered, searched, ordered and paged.
/// <para>
/// The queue page used to fetch <em>every</em> job every two seconds and rebuild a row for each one. On
/// a library large enough to produce tens of thousands of jobs that is megabytes of JSON per poll and a
/// table hundreds of screens long — the complaint in issue #6. Slicing server-side means a view only
/// ever transfers and renders what fits on the screen, however long the history grows.
/// </para>
/// <para>Pure over a job list, so the ordering and paging rules are unit-testable on their own.</para>
/// </summary>
internal static class JobQuery
{
    /// <summary>Rows per page when a caller does not ask for a specific size.</summary>
    internal const int DefaultLimit = 50;

    /// <summary>Upper bound on a page, so one request cannot ask for the whole history again.</summary>
    internal const int MaxLimit = 500;

    /// <summary>
    /// Whether a status means the job is done with, in either direction. The live queue is everything
    /// that is not.
    /// </summary>
    /// <param name="status">The job status.</param>
    /// <returns><c>true</c> when the job has finished.</returns>
    internal static bool IsFinished(JobStatus status)
    {
        return status is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled or JobStatus.Skipped;
    }

    /// <summary>
    /// Maps the filter name a page sends to the enum, accepting the words each view calls itself.
    /// Anything unrecognised — including nothing at all — means everything.
    /// </summary>
    /// <param name="value">The filter name from the query string.</param>
    /// <returns>The parsed filter.</returns>
    internal static JobFilter ParseFilter(string? value)
    {
        return (value ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "active" or "queue" or "queued" or "pending" => JobFilter.Active,
            "finished" or "history" or "done" => JobFilter.Finished,
            _ => JobFilter.All
        };
    }

    /// <summary>
    /// Returns one page of jobs plus the total the filter matched, so a page can show "x-y of z" and
    /// know whether a next page exists.
    /// </summary>
    /// <param name="jobs">The full job list.</param>
    /// <param name="filter">Which slice to return.</param>
    /// <param name="search">Optional text matched against the display name and source path.</param>
    /// <param name="startIndex">How many matching jobs to skip.</param>
    /// <param name="limit">Maximum jobs to return, clamped to 1..<see cref="MaxLimit"/>.</param>
    /// <returns>The page and the total number of matches.</returns>
    internal static (IReadOnlyList<TranscodeJob> Items, int TotalRecordCount) Page(
        IReadOnlyList<TranscodeJob> jobs,
        JobFilter filter,
        string? search,
        int startIndex,
        int limit)
    {
        var ordered = Order(Match(jobs, filter, search), filter);

        // A start index past the end is not an error — it is what a page shows after the jobs it was
        // looking at were removed or trimmed — so it yields an empty page, not a clamped last page that
        // would silently jump the reader somewhere else.
        var start = Math.Max(startIndex, 0);
        var size = Math.Clamp(limit, 1, MaxLimit);

        return (start >= ordered.Count
            ? Array.Empty<TranscodeJob>()
            : ordered.GetRange(start, Math.Min(size, ordered.Count - start)), ordered.Count);
    }

    /// <summary>
    /// Everything the control-center page shows, from a single walk of the list: the per-status counts,
    /// what is running, what runs next, and what finished most recently.
    /// <para>
    /// Counting and slicing in one pass is not just cheaper, it is the only way the two can agree.
    /// Counting first and slicing afterwards let an encode finish in between, and the page then drew
    /// "Processing: 1" directly above "Idle — nothing is encoding right now" until the next poll.
    /// </para>
    /// </summary>
    /// <param name="jobs">The full job list.</param>
    /// <param name="upNextCount">How many pending jobs to preview.</param>
    /// <param name="recentCount">How many finished jobs to preview.</param>
    /// <returns>The counts and the three slices.</returns>
    internal static (JobCounts Counts, IReadOnlyList<TranscodeJob> Processing, IReadOnlyList<TranscodeJob> UpNext, IReadOnlyList<TranscodeJob> Recent) Overview(
        IReadOnlyList<TranscodeJob> jobs,
        int upNextCount,
        int recentCount)
    {
        var processing = new List<TranscodeJob>();
        var pending = new List<TranscodeJob>();
        var finished = new List<TranscodeJob>();
        int completed = 0, failed = 0, cancelled = 0, skipped = 0;

        foreach (var job in jobs)
        {
            // Read once. The executor mutates these very instances while this runs, so re-reading Status
            // in a later pass can put one job in two buckets at once — running AND recently finished.
            var status = job.Status;
            switch (status)
            {
                case JobStatus.Pending: pending.Add(job); break;
                case JobStatus.Processing: processing.Add(job); break;
                case JobStatus.Completed: finished.Add(job); completed++; break;
                case JobStatus.Failed: finished.Add(job); failed++; break;
                case JobStatus.Cancelled: finished.Add(job); cancelled++; break;
                case JobStatus.Skipped: finished.Add(job); skipped++; break;
                default: break;
            }
        }

        var counts = new JobCounts(pending.Count, processing.Count, completed, failed, cancelled, skipped, jobs.Count);

        // Every running job is listed, not the first N: with a concurrency of 2 or more, hiding one of
        // them would leave the page claiming less work is happening than really is.
        processing.Sort((a, b) => (a.StartedUtc ?? a.CreatedUtc).CompareTo(b.StartedUtc ?? b.CreatedUtc));

        return (
            counts,
            processing,
            pending.OrderBy(j => j.CreatedUtc).Take(Math.Max(upNextCount, 0)).ToList(),
            finished.OrderByDescending(j => j.FinishedUtc ?? j.CreatedUtc)
                .ThenByDescending(j => j.CreatedUtc)
                .Take(Math.Max(recentCount, 0))
                .ToList());
    }

    /// <summary>
    /// Counts each status in a single pass. Both the status and the overview endpoints are polled every
    /// couple of seconds, so this is deliberately one walk rather than a Count() per status.
    /// </summary>
    /// <param name="jobs">The full job list.</param>
    /// <returns>The per-status counts.</returns>
    internal static JobCounts Count(IReadOnlyList<TranscodeJob> jobs)
    {
        int pending = 0, processing = 0, completed = 0, failed = 0, cancelled = 0, skipped = 0;
        foreach (var job in jobs)
        {
            switch (job.Status)
            {
                case JobStatus.Pending: pending++; break;
                case JobStatus.Processing: processing++; break;
                case JobStatus.Completed: completed++; break;
                case JobStatus.Failed: failed++; break;
                case JobStatus.Cancelled: cancelled++; break;
                case JobStatus.Skipped: skipped++; break;
                default: break;
            }
        }

        return new JobCounts(pending, processing, completed, failed, cancelled, skipped, jobs.Count);
    }

    private static List<Snapshot> Match(IReadOnlyList<TranscodeJob> jobs, JobFilter filter, string? search)
    {
        var term = (search ?? string.Empty).Trim();
        var matched = new List<Snapshot>();

        foreach (var job in jobs)
        {
            var status = job.Status;
            var finished = IsFinished(status);
            if ((filter == JobFilter.Active && finished) || (filter == JobFilter.Finished && !finished))
            {
                continue;
            }

            // Both the title and the path are searched: an admin chasing one bad encode usually has the
            // filename, while one checking a whole show has the title.
            if (term.Length > 0
                && !job.DisplayName.Contains(term, StringComparison.OrdinalIgnoreCase)
                && !job.SourcePath.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            matched.Add(new Snapshot(job, status));
        }

        return matched;
    }

    // The live queue reads as a running order — what is encoding, then what is next, oldest first, which
    // is the order ClaimNextPending really picks them in. History reads as a log, newest first. "All"
    // is both, in that order, so the top of the list is always the part that is still moving.
    // Ties on FinishedUtc fall back to newest-queued first, so History keeps the order its title
    // promises. They are not rare: "Cancel all" stamps one instant into every pending job at once, and
    // without the tiebreak a stable sort resolved that block to insertion order — 50,000 cancelled jobs
    // listed OLDEST first on a newest-first tab, with the most recent one a thousand pages in.
    // Pending is deliberately left on CreatedUtc alone: a stable sort resolves those ties to list order,
    // which is exactly the job ClaimNextPending's MinBy picks, and any extra tiebreak here would make the
    // page promise a running order the executor does not follow.
    private static List<TranscodeJob> Order(List<Snapshot> matched, JobFilter filter)
    {
        if (filter == JobFilter.Finished)
        {
            return matched
                .OrderByDescending(s => s.Job.FinishedUtc ?? s.Job.CreatedUtc)
                .ThenByDescending(s => s.Job.CreatedUtc)
                .Select(s => s.Job)
                .ToList();
        }

        return matched.Where(s => s.Status == JobStatus.Processing).OrderBy(s => s.Job.StartedUtc ?? s.Job.CreatedUtc)
            .Concat(matched.Where(s => s.Status == JobStatus.Pending).OrderBy(s => s.Job.CreatedUtc))
            .Concat(matched.Where(s => IsFinished(s.Status))
                .OrderByDescending(s => s.Job.FinishedUtc ?? s.Job.CreatedUtc)
                .ThenByDescending(s => s.Job.CreatedUtc))
            .Select(s => s.Job)
            .ToList();
    }

    // A job paired with the status it had when this query started. The queue hands out the live
    // TranscodeJob instances and the executor mutates them in place, so a status re-read in a later pass
    // can disagree with the first one: a job that finishes mid-query was emitted by the "processing" pass
    // AND by the "finished" pass — the same title twice on one page, with a total one too high — while a
    // job claimed mid-query fell between the "pending" and "processing" passes and appeared on no page at
    // all. Reading it once, here, is what makes each job land in exactly one bucket.
    private readonly record struct Snapshot(TranscodeJob Job, JobStatus Status);
}
