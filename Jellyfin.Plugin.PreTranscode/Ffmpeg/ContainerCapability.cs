namespace Jellyfin.Plugin.PreTranscode.Ffmpeg;

/// <summary>
/// An output container/muxer.
/// </summary>
public class ContainerCapability
{
    /// <summary>
    /// Gets or sets the muxer name (e.g. <c>mp4</c>, <c>matroska</c>).
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the human-readable muxer description.
    /// </summary>
    public string Description { get; set; } = string.Empty;
}
