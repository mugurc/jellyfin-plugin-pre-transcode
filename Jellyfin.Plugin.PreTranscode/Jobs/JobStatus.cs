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

    /// <summary>
    /// Finished without applying an output: the source already complied, its output already existed, the
    /// transcode was not smaller, or the source changed while it was encoded (the status detail says which).
    /// </summary>
    Skipped
}
