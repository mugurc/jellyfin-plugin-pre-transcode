using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.PreTranscode.Media;

/// <summary>
/// A compact, probe-derived description of a source media file. Populated from Jellyfin's item
/// metadata and/or an ffprobe pass, then consumed by the rule engine and the ffmpeg command builder.
/// </summary>
public class MediaProbeInfo
{
    /// <summary>
    /// Gets or sets the absolute source file path.
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the source container/format (e.g. <c>mkv</c>, <c>mp4</c>).
    /// </summary>
    public string Container { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the duration in seconds.
    /// </summary>
    public double DurationSeconds { get; set; }

    /// <summary>
    /// Gets or sets the file size in bytes.
    /// </summary>
    public long FileSizeBytes { get; set; }

    /// <summary>
    /// Gets or sets the primary video stream codec (e.g. <c>hevc</c>).
    /// </summary>
    public string VideoCodec { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the video width in pixels.
    /// </summary>
    public int Width { get; set; }

    /// <summary>
    /// Gets or sets the video height in pixels.
    /// </summary>
    public int Height { get; set; }

    /// <summary>
    /// Gets or sets the video bitrate in kbps (0 when unknown — e.g. Matroska stores no per-stream
    /// bitrate, and the format-level total is not attributed to video when other streams are present).
    /// </summary>
    public int VideoBitrateKbps { get; set; }

    /// <summary>
    /// Gets or sets the video frame rate.
    /// </summary>
    public double VideoFramerate { get; set; }

    /// <summary>
    /// Gets or sets the video pixel format (e.g. <c>yuv420p10le</c>).
    /// </summary>
    public string PixelFormat { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the video is HDR (HDR10/HLG/etc.).
    /// </summary>
    public bool IsHdr { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the video carries Dolby Vision.
    /// </summary>
    public bool IsDolbyVision { get; set; }

    /// <summary>
    /// Gets or sets the primary audio stream codec (e.g. <c>truehd</c>).
    /// </summary>
    public string AudioCodec { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the primary audio channel count.
    /// </summary>
    public int AudioChannels { get; set; }

    /// <summary>
    /// Gets or sets the primary audio bitrate in kbps.
    /// </summary>
    public int AudioBitrateKbps { get; set; }

    /// <summary>
    /// Gets or sets every audio track (all languages), in source order. The command builder uses this
    /// to preserve other-language audio, copying tracks already in the target codec and re-encoding
    /// only the rest. Empty when the probe did not enumerate streams.
    /// </summary>
    public IReadOnlyList<AudioStreamInfo> AudioStreams { get; set; } = Array.Empty<AudioStreamInfo>();

    /// <summary>
    /// Gets or sets every subtitle track (all languages), in source order.
    /// </summary>
    public IReadOnlyList<SubtitleStreamInfo> SubtitleStreams { get; set; } = Array.Empty<SubtitleStreamInfo>();

    /// <summary>
    /// Gets the file size in megabytes (derived from <see cref="FileSizeBytes"/>).
    /// </summary>
    public double FileSizeMb => FileSizeBytes / (1024d * 1024d);
}
