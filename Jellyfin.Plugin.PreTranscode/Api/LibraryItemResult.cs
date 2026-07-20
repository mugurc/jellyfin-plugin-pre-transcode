namespace Jellyfin.Plugin.PreTranscode.Api;

/// <summary>
/// A single library item (movie or episode) returned by the manual single-item search.
/// </summary>
public class LibraryItemResult
{
    /// <summary>
    /// Gets or sets the Jellyfin item id (N-format guid).
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a friendly display label (a movie title, or "Series - SxxEyy - Title").
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the item type, "Movie" or "Episode".
    /// </summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the source file path.
    /// </summary>
    public string Path { get; set; } = string.Empty;
}
