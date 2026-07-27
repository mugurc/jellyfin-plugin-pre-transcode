using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.PreTranscode.Ffmpeg;

/// <summary>
/// Describes the valid preset/speed values for a specific encoder, as discovered from ffmpeg.
/// </summary>
public class EncoderPresetInfo
{
    /// <summary>
    /// Gets or sets the encoder these presets belong to.
    /// </summary>
    public string Encoder { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets how the presets are expressed for this encoder.
    /// </summary>
    public PresetKind Kind { get; set; }

    /// <summary>
    /// Gets or sets the named preset values (when <see cref="Kind"/> is <see cref="PresetKind.NamedList"/>).
    /// </summary>
    public IReadOnlyList<string> Values { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Gets or sets the inclusive lower bound (when <see cref="Kind"/> is <see cref="PresetKind.IntRange"/>).
    /// </summary>
    public int? RangeMin { get; set; }

    /// <summary>
    /// Gets or sets the inclusive upper bound (when <see cref="Kind"/> is <see cref="PresetKind.IntRange"/>).
    /// </summary>
    public int? RangeMax { get; set; }

    /// <summary>
    /// Gets or sets the encoder's default preset, if reported.
    /// </summary>
    public string? Default { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the values were discovered from ffmpeg (<c>true</c>)
    /// or supplied as a documented fallback because ffmpeg does not enumerate them (<c>false</c>).
    /// </summary>
    public bool FromFfmpeg { get; set; }

    /// <summary>
    /// Gets or sets the pixel formats this encoder accepts, exactly as <c>ffmpeg -h encoder=…</c>
    /// reports them. Empty when ffmpeg does not list any (VAAPI, for instance, reports only its
    /// hardware surface type).
    /// <para>
    /// Read to decide whether a 10-bit source can keep its bit depth: the correct 10-bit format is
    /// encoder-specific and cannot be assumed — verified against ffmpeg 8.1.1, libx265/libsvtav1/libaom
    /// take <c>yuv420p10le</c>, the NVENC/QSV/AMF families take <c>p010le</c>, and <c>h264_qsv</c>
    /// offers no 10-bit format at all.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> PixelFormats { get; set; } = Array.Empty<string>();
}
