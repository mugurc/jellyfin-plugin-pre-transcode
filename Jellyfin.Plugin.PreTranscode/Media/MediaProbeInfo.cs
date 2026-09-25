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
    /// Gets or sets the video width in pixels as it is <b>displayed</b> — the coded width less any
    /// container crop. Equal to <see cref="CodedWidth"/> for the overwhelming majority of files.
    /// </summary>
    /// <remarks>
    /// This is deliberately the displayed size rather than the coded one, because every consumer here
    /// (rule conditions, the compliance check, the scale filter) is reasoning about the picture a viewer
    /// ends up with, and ffmpeg applies container cropping by itself from 7.1 onwards. A Matroska file
    /// carrying <c>PixelCrop*</c> elements reports the uncropped size in ffprobe's <c>width</c>/
    /// <c>height</c> and the crop separately as side data, so taking those fields at face value describes
    /// a frame that will not exist in the output. See <see cref="CodedWidth"/> for the raw figure.
    /// </remarks>
    public int Width { get; set; }

    /// <summary>
    /// Gets or sets the video height in pixels as it is <b>displayed</b> — the coded height less any
    /// container crop. Equal to <see cref="CodedHeight"/> for the overwhelming majority of files.
    /// </summary>
    public int Height { get; set; }

    /// <summary>
    /// Gets or sets the coded video width in pixels, exactly as ffprobe reported it, before any
    /// container crop is applied. Equal to <see cref="Width"/> unless the container carries a crop.
    /// </summary>
    public int CodedWidth { get; set; }

    /// <summary>
    /// Gets or sets the coded video height in pixels, exactly as ffprobe reported it, before any
    /// container crop is applied. Equal to <see cref="Height"/> unless the container carries a crop.
    /// </summary>
    /// <remarks>
    /// Kept because an ffmpeg older than 7.1 does <b>not</b> apply container cropping, so an output
    /// verified against the displayed size alone would be rejected on those servers. The verifier
    /// accepts either shape for that reason.
    /// </remarks>
    public int CodedHeight { get; set; }

    /// <summary>
    /// Gets a value indicating whether the container carries a crop, i.e. the displayed size differs
    /// from the coded size.
    /// </summary>
    public bool HasContainerCrop => CodedWidth != Width || CodedHeight != Height;

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
    /// Gets or sets the video's bits per sample (8, 10, 12 …), or 0 when the probe could not determine
    /// it. Drives the output pixel-format choice: re-encoding a 10-bit source as 8-bit throws away
    /// precision, and doing so while keeping the source's HDR tags produces visible banding.
    /// </summary>
    public int BitDepth { get; set; }

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
