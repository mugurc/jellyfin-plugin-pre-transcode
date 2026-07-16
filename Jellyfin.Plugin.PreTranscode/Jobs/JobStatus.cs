namespace Jellyfin.Plugin.PreTranscode.Jobs;

/// <summary>
/// Lifecycle state of a transcode job.
/// </summary>
public enum JobStatus
{
    /// <summary>Waiting in the queue.</summary>
    Pending,

    /// <summary>Currently being processed by a worker.</summary>
    Processing,

    /// <summary>Finished successfully.</summary>
    Completed,

    /// <summary>Failed (see the error message / log excerpt).</summary>
    Failed,

    /// <summary>Cancelled by an admin.</summary>
    Cancelled,

    /// <summary>Skipped because the source already complies with the target profile.</summary>
    Skipped
}
