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
}
