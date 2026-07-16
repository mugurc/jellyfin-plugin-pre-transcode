namespace Jellyfin.Plugin.PreTranscode.Ffmpeg;

/// <summary>
/// A concrete ffmpeg encoder implementation.
/// </summary>
public class EncoderCapability
{
    /// <summary>
    /// Gets or sets the encoder name (e.g. <c>libx264</c>).
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether this encoder is hardware-accelerated (heuristic, based on the name).
    /// </summary>
    public bool IsHardware { get; set; }
}
