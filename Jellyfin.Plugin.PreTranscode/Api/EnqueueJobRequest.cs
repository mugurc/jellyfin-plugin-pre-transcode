namespace Jellyfin.Plugin.PreTranscode.Api;

/// <summary>
/// Request body for manually enqueuing a pre-transcode job.
/// </summary>
public class EnqueueJobRequest
{
    /// <summary>
    /// Gets or sets the absolute source file path to transcode.
    /// </summary>
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the encoding profile id to use. Empty means the global default profile, and the job is
    /// stored with that profile's id; an id that names no configured profile is rejected with 400.
    /// </summary>
    public string ProfileId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the related Jellyfin item id (optional).
    /// </summary>
    public string ItemId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a display name for the queue UI (optional).
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;
}
