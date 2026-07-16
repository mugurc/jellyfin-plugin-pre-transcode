using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.PreTranscode.Configuration;
using Jellyfin.Plugin.PreTranscode.Media;

namespace Jellyfin.Plugin.PreTranscode.Rules;

/// <summary>
/// Cheap idempotency check: decides whether a source file already satisfies a target profile, so
/// pre-transcoding it would be a no-op and it can be skipped.
/// </summary>
internal static class ProfileComplianceChecker
{
    // True when the source already matches the profile in every dimension it would otherwise change
    // (codec, container, resolution cap, audio codec/channels, HDR tone-mapping).
    public static bool IsAlreadyCompliant(EncodingProfile profile, MediaProbeInfo info, IReadOnlyList<ResolutionPreset> presets)
    {
        return !NeedsWork(profile, info, presets, out _);
    }

    // True when at least one dimension of the profile would change the source; 'reason' describes the first.
    public static bool NeedsWork(EncodingProfile profile, MediaProbeInfo info, IReadOnlyList<ResolutionPreset> presets, out string reason)
    {
        if (!IsCopy(profile.VideoCodec) && !Same(info.VideoCodec, profile.VideoCodec))
        {
            reason = "video codec differs";
            return true;
        }

        if (!IsCopy(profile.AudioCodec) && !Same(info.AudioCodec, profile.AudioCodec))
        {
            reason = "audio codec differs";
            return true;
        }

        if (!ContainerMatches(info.Container, profile.Container))
        {
            reason = "container differs";
            return true;
        }

        if (ExceedsResolution(profile, info, presets))
        {
            reason = "resolution exceeds target";
            return true;
        }

        if (profile.TonemapHdr && info.IsHdr)
        {
            reason = "HDR would be tone-mapped";
            return true;
        }

        if (ExceedsChannels(profile, info))
        {
            reason = "audio channels exceed cap";
            return true;
        }

        reason = string.Empty;
        return false;
    }

    private static bool ExceedsResolution(EncodingProfile profile, MediaProbeInfo info, IReadOnlyList<ResolutionPreset> presets)
    {
        switch (profile.ResolutionMode)
        {
            case ResolutionMode.CapWidth:
                return profile.MaxWidth > 0 && info.Width > profile.MaxWidth;
            case ResolutionMode.CapHeight:
                return profile.MaxHeight > 0 && info.Height > profile.MaxHeight;
            case ResolutionMode.CapLongestEdge:
                return profile.MaxWidth > 0 && Math.Max(info.Width, info.Height) > profile.MaxWidth;
            case ResolutionMode.UsePreset:
                var preset = presets.FirstOrDefault(p => string.Equals(p.Id, profile.ResolutionPresetId, StringComparison.Ordinal));
                return preset is not null && (info.Width > preset.Width || info.Height > preset.Height);
            default:
                return false;
        }
    }

    private static bool ExceedsChannels(EncodingProfile profile, MediaProbeInfo info)
    {
        if (IsCopy(profile.AudioCodec))
        {
            return false;
        }

        return profile.ChannelPolicy switch
        {
            AudioChannelPolicy.CapStereo => info.AudioChannels > 2,
            AudioChannelPolicy.Cap51 => info.AudioChannels > 6,
            AudioChannelPolicy.CapCustom => profile.MaxAudioChannels > 0 && info.AudioChannels > profile.MaxAudioChannels,
            _ => false
        };
    }

    private static bool ContainerMatches(string sourceContainer, string targetContainer)
    {
        if (Same(sourceContainer, targetContainer))
        {
            return true;
        }

        // ffprobe reports comma lists (e.g. "matroska,webm") and uses names that differ from common
        // extensions; treat known aliases as equivalent.
        var aliases = ContainerAliases(targetContainer);
        var sourceParts = sourceContainer.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return sourceParts.Any(part => aliases.Contains(part, StringComparer.OrdinalIgnoreCase));
    }

    private static string[] ContainerAliases(string container)
    {
        return container.ToLowerInvariant() switch
        {
            "matroska" or "mkv" => new[] { "matroska", "mkv", "webm" },
            "mp4" => new[] { "mp4", "mov", "m4v", "isom", "mp42" },
            "mov" => new[] { "mov", "mp4", "qt" },
            _ => new[] { container }
        };
    }

    private static bool IsCopy(string codec)
    {
        return string.Equals(codec, "copy", StringComparison.OrdinalIgnoreCase);
    }

    private static bool Same(string a, string b)
    {
        return string.Equals(a ?? string.Empty, b ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }
}
