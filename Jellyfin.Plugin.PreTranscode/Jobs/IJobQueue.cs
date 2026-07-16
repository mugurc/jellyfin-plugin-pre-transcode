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
    /// Adds a job unless an active (pending/processing) job already exists for the same source path.
    /// </summary>
    /// <param name="job">The job to add.</param>
    /// <returns><c>true</c> if enqueued; <c>false</c> if a duplicate was skipped.</returns>
    bool Enqueue(TranscodeJob job);

    /// <summary>
    /// Persists changes to an existing job.
    /// </summary>
    /// <param name="job">The job to update.</param>
    void Update(TranscodeJob job);

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
    /// Resets a finished job back to pending so it will be retried.
    /// </summary>
    /// <param name="id">The job id.</param>
    /// <returns><c>true</c> if the job existed.</returns>
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
