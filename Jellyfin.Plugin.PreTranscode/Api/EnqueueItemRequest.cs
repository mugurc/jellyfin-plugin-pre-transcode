namespace Jellyfin.Plugin.PreTranscode.Api;

/// <summary>
/// Request body for manually enqueuing a single library item (movie or episode) by its Jellyfin id.
/// </summary>
public class EnqueueItemRequest
{
    /// <summary>
    /// Gets or sets the Jellyfin item id to transcode.
    /// </summary>
    public string ItemId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the encoding profile id to use (empty = global default).
    /// </summary>
    public string ProfileId { get; set; } = string.Empty;
}
