namespace Jellyfin.Plugin.PreTranscode.Media;

/// <summary>
/// A per-stream description of a single audio track. Unlike the scalar first-audio fields on
/// <see cref="MediaProbeInfo"/> (kept for the rule engine and the cheap compliance check), this lets
/// the command builder decide, track by track, whether an other-language audio stream can be copied
/// verbatim or must be re-encoded.
/// </summary>
public sealed class AudioStreamInfo
{
    /// <summary>
    /// Gets or sets the audio codec (e.g. <c>aac</c>, <c>ac3</c>, <c>truehd</c>).
    /// </summary>
    public string Codec { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the channel count.
    /// </summary>
    public int Channels { get; set; }

    /// <summary>
    /// Gets or sets the bitrate in kbps (0 when ffprobe does not report one).
    /// </summary>
    public int BitrateKbps { get; set; }

    /// <summary>
    /// Gets or sets the ISO language tag (e.g. <c>eng</c>, <c>tur</c>), empty when untagged.
    /// </summary>
    public string Language { get; set; } = string.Empty;
}
