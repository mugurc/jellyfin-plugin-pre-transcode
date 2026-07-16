namespace Jellyfin.Plugin.PreTranscode.Configuration;

/// <summary>
/// A named, editable resolution preset (e.g. "1080p"). Stored as data, not hardcoded, so admins
/// can add, remove or edit presets freely.
/// </summary>
public class ResolutionPreset
{
    /// <summary>
    /// Gets or sets the stable identifier.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the display name (e.g. "1080p").
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the target width in pixels.
    /// </summary>
    public int Width { get; set; }

    /// <summary>
    /// Gets or sets the target height in pixels.
    /// </summary>
    public int Height { get; set; }
}
