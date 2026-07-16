namespace Jellyfin.Plugin.PreTranscode.Media;

/// <summary>
/// A per-stream description of a single subtitle track, used to report how many subtitle tracks a
/// transcode will carry across (all of them, losslessly, for a Matroska output).
/// </summary>
public sealed class SubtitleStreamInfo
{
    /// <summary>
    /// Gets or sets the subtitle codec (e.g. <c>subrip</c>, <c>ass</c>, <c>hdmv_pgs_subtitle</c>).
    /// </summary>
    public string Codec { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the ISO language tag (e.g. <c>eng</c>, <c>tur</c>), empty when untagged.
    /// </summary>
    public string Language { get; set; } = string.Empty;
}
