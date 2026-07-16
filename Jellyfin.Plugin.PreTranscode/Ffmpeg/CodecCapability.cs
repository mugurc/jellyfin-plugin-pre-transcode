using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.PreTranscode.Ffmpeg;

/// <summary>
/// An encodable codec and the concrete encoder implementations that can produce it.
/// </summary>
public class CodecCapability
{
    /// <summary>
    /// Gets or sets the codec name (e.g. <c>h264</c>).
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the human-readable codec description.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the media type of the codec.
    /// </summary>
    public CodecMediaType MediaType { get; set; }

    /// <summary>
    /// Gets or sets the encoder implementations available for this codec (e.g. <c>libx264</c>, <c>h264_nvenc</c>).
    /// </summary>
    public IReadOnlyList<EncoderCapability> Encoders { get; set; } = Array.Empty<EncoderCapability>();
}
