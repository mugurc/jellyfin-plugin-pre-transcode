namespace Jellyfin.Plugin.PreTranscode.Jobs;

/// <summary>
/// Runtime control over in-flight jobs (implemented by the background queue processor). Exposed as a
/// public interface so the API controller can depend on it without referencing the internal processor.
/// </summary>
public interface IQueueController
{
    /// <summary>
    /// Cancels a job whether it is still queued or actively running.
    /// </summary>
    /// <param name="id">The job id.</param>
    /// <returns><c>true</c> if the job existed.</returns>
    bool CancelJob(string id);

    /// <summary>
    /// Pauses the queue: stops new jobs from being claimed and suspends any running encode at the OS
    /// level, freeing CPU without losing progress.
    /// </summary>
    void Pause();

    /// <summary>
    /// Resumes the queue: continues any suspended encode where it left off and lets new jobs be claimed.
    /// </summary>
    void Resume();
}
