namespace Jellyfin.Plugin.PreTranscode.Jobs;

/// <summary>
/// Per-status job counts for the summary line.
/// </summary>
/// <param name="Pending">Jobs waiting to run.</param>
/// <param name="Processing">Jobs currently encoding.</param>
/// <param name="Completed">Jobs that finished successfully.</param>
/// <param name="Failed">Jobs that failed.</param>
/// <param name="Cancelled">Jobs cancelled by an admin.</param>
/// <param name="Skipped">Jobs skipped as already compliant or already done.</param>
/// <param name="Total">Every job the queue holds.</param>
internal readonly record struct JobCounts(
    int Pending,
    int Processing,
    int Completed,
    int Failed,
    int Cancelled,
    int Skipped,
    int Total);
