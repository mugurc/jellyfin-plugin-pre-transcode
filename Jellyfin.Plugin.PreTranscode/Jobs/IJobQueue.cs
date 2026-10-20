using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.PreTranscode.Jobs;

/// <summary>
/// A persistent, restart-safe queue of <see cref="TranscodeJob"/>s.
/// </summary>
public interface IJobQueue
{
    /// <summary>
    /// Gets or sets a value indicating whether the queue is paused (no new jobs are claimed).
    /// </summary>
    bool IsPaused { get; set; }

    /// <summary>
    /// Returns a snapshot of all jobs.
    /// </summary>
    /// <returns>All jobs.</returns>
    IReadOnlyList<TranscodeJob> GetJobs();

    /// <summary>
    /// Gets a single job by id, or <c>null</c>.
    /// </summary>
    /// <param name="id">The job id.</param>
    /// <returns>The job, or <c>null</c>.</returns>
    TranscodeJob? Get(string id);

    /// <summary>
    /// Adds a job unless an active (pending/processing) job already exists for the same source path, or
    /// the optional <paramref name="isRedundant"/> predicate — evaluated atomically under the same lock,
    /// with the current job list — reports the work is no longer needed. The predicate closes the gap
    /// where a source is re-evaluated while an earlier job for it completes: the caller's own pre-checks
    /// run before an <c>await</c>, so only a check performed together with the add is race-free.
    /// </summary>
    /// <param name="job">The job to add.</param>
    /// <param name="isRedundant">Optional final redundancy check, run under the lock with the live job list.</param>
    /// <returns><c>true</c> if enqueued; <c>false</c> if a duplicate/redundant job was skipped.</returns>
    bool Enqueue(TranscodeJob job, Func<IReadOnlyList<TranscodeJob>, bool>? isRedundant = null);

    /// <summary>
    /// Persists changes to an existing job.
    /// </summary>
    /// <param name="job">The job to update.</param>
    void Update(TranscodeJob job);

    /// <summary>
    /// Applies <paramref name="change"/> to a job and persists it as one step, so that no other queue
    /// operation can observe the job half-changed. A job moving to a final state must go through this:
    /// trimming the history drops the oldest finished jobs by <see cref="TranscodeJob.FinishedUtc"/>, and
    /// a job seen with its final status but not yet its finish time sorts as ancient — it was dropped,
    /// and the update that followed found nothing left to save.
    /// <para>
    /// The default applies the change and then calls <see cref="Update(TranscodeJob)"/>, which is all an
    /// implementation without a lock can offer; it exists so that adding this member did not break other
    /// implementations of this public interface.
    /// </para>
    /// </summary>
    /// <param name="job">The job to change.</param>
    /// <param name="change">The change, run under the queue's lock; it must not block.</param>
    void Update(TranscodeJob job, Action<TranscodeJob> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        change(job);
        Update(job);
    }

    /// <summary>
    /// Claims the next pending job (marking it processing), or <c>null</c> if none/paused.
    /// </summary>
    /// <returns>The claimed job, or <c>null</c>.</returns>
    TranscodeJob? ClaimNextPending();

    /// <summary>
    /// Requests cancellation of a job.
    /// </summary>
    /// <param name="id">The job id.</param>
    /// <returns><c>true</c> if the job existed.</returns>
    bool Cancel(string id);

    /// <summary>
    /// Cancels every still-pending job in one pass, and persists the result once.
    /// <para>
    /// "Cancel all" used to call <see cref="Cancel"/> per job, and every one of those rewrote the whole
    /// queue file — on a large backlog that is one multi-megabyte write per cancelled job. Jobs that are
    /// already <see cref="JobStatus.Processing"/> are not touched here: aborting a running encode is the
    /// queue processor's job, and there are only ever a handful of those.
    /// </para>
    /// </summary>
    /// <returns>How many jobs were cancelled.</returns>
    int CancelAllPending();

    /// <summary>
    /// Resets a finished job back to pending so it will be retried — unless it is still running, or
    /// another job for the same source is already pending or running (the same one-active-job-per-source
    /// rule <see cref="Enqueue"/> applies).
    /// </summary>
    /// <param name="id">The job id.</param>
    /// <returns><c>true</c> if the job was requeued; <c>false</c> if it does not exist or was refused.</returns>
    bool Requeue(string id);

    /// <summary>
    /// Removes a job from the queue.
    /// </summary>
    /// <param name="id">The job id.</param>
    /// <returns><c>true</c> if the job existed.</returns>
    bool Remove(string id);

    /// <summary>
    /// Removes all completed/failed/cancelled/skipped jobs.
    /// </summary>
    void ClearFinished();
}
