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
}
