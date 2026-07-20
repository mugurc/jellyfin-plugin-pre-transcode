using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.PreTranscode.Configuration;
using Jellyfin.Plugin.PreTranscode.Media;

namespace Jellyfin.Plugin.PreTranscode.Encoding;

/// <summary>
/// Pure computation of the ffmpeg <c>scale</c> filter needed to satisfy a profile's resolution
/// policy for a given source. Only ever downscales; returns <c>null</c> when no scaling is required.
/// </summary>
internal static class ResolutionCalculator
{
    public static string? BuildScaleFilter(EncodingProfile profile, MediaProbeInfo source, IReadOnlyList<ResolutionPreset> presets)
    {
        switch (profile.ResolutionMode)
        {
            case ResolutionMode.CapWidth:
                if (profile.MaxWidth <= 0 || source.Width <= profile.MaxWidth)
                {
                    return null;
                }

                return "scale=" + N(Even(profile.MaxWidth)) + ":-2";

            case ResolutionMode.CapHeight:
                if (profile.MaxHeight <= 0 || source.Height <= profile.MaxHeight)
                {
                    return null;
                }

                return "scale=-2:" + N(Even(profile.MaxHeight));

            case ResolutionMode.CapLongestEdge:
                if (profile.MaxWidth <= 0)
                {
                    return null;
                }

                if (source.Width >= source.Height)
                {
                    return source.Width > profile.MaxWidth ? "scale=" + N(Even(profile.MaxWidth)) + ":-2" : null;
                }

                return source.Height > profile.MaxWidth ? "scale=-2:" + N(Even(profile.MaxWidth)) : null;

            case ResolutionMode.UsePreset:
                var preset = presets.FirstOrDefault(p => string.Equals(p.Id, profile.ResolutionPresetId, StringComparison.Ordinal));
                if (preset is null || (source.Width <= preset.Width && source.Height <= preset.Height))
                {
                    return null;
                }

                return "scale=w=" + N(preset.Width) + ":h=" + N(preset.Height)
                    + ":force_original_aspect_ratio=decrease:force_divisible_by=2";

            default:
                return null;
        }
    }

    // Rounds a capped edge down to the nearest even value (minimum 2). The fixed edge of a "scale=W:-2"
    // filter is emitted verbatim, and yuv420p (4:2:0) output requires even dimensions — an odd cap like
    // MaxWidth=1281 would otherwise make ffmpeg abort with "width not divisible by 2".
    private static int Even(int value)
    {
        return Math.Max(2, value - (value % 2));
    }

    private static string N(int value)
    {
        return value.ToString(CultureInfo.InvariantCulture);
    }
}
