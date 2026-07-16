namespace Jellyfin.Plugin.PreTranscode.Ffmpeg;

/// <summary>
/// The media type a codec produces.
/// </summary>
public enum CodecMediaType
{
    /// <summary>Video.</summary>
    Video,

    /// <summary>Audio.</summary>
    Audio,

    /// <summary>Subtitle.</summary>
    Subtitle,

    /// <summary>Anything else (data, attachment, ...).</summary>
    Other
}
