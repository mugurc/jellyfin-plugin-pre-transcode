using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.PreTranscode.Ffmpeg;

/// <summary>
/// Everything the plugin discovered about the system's ffmpeg binary. All lists are populated
/// by probing the actual binary, never hardcoded, so newly-supported codecs/encoders appear
/// automatically.
/// </summary>
public class FfmpegCapabilities
{
    /// <summary>
    /// Gets or sets the resolved ffmpeg binary path.
    /// </summary>
    public string FfmpegPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the ffmpeg version string.
    /// </summary>
    public string FfmpegVersion { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the encodable video codecs (each with its available encoder implementations).
    /// </summary>
    public IReadOnlyList<CodecCapability> VideoCodecs { get; set; } = Array.Empty<CodecCapability>();

    /// <summary>
    /// Gets or sets the encodable audio codecs (each with its available encoder implementations).
    /// </summary>
    public IReadOnlyList<CodecCapability> AudioCodecs { get; set; } = Array.Empty<CodecCapability>();

    /// <summary>
    /// Gets or sets the available output containers (muxers).
    /// </summary>
    public IReadOnlyList<ContainerCapability> Containers { get; set; } = Array.Empty<ContainerCapability>();

    /// <summary>
    /// Gets or sets the available tone-mapping algorithms exposed by ffmpeg's tonemap filter.
    /// </summary>
    public IReadOnlyList<string> TonemapAlgorithms { get; set; } = Array.Empty<string>();
}
