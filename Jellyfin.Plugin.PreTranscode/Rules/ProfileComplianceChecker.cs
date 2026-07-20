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

        if (!IsCopy(profile.AudioCodec) && AudioCodecNeedsWork(profile, info))
        {
            reason = "audio codec differs";
            return true;
        }

        if (!ContainerMatches(info.Container, profile.Container))
        {
            reason = "container differs";
            return true;
        }

        // Resolution and tone-mapping are only ever applied to a re-encoded video stream; when the
        // profile copies the video verbatim the builder emits no scale/tonemap filter, so testing
        // these against a copy profile would flag the encoder's own output as non-compliant forever.
        if (!IsCopy(profile.VideoCodec) && ExceedsResolution(profile, info, presets))
        {
            reason = "resolution exceeds target";
            return true;
        }

        if (!IsCopy(profile.VideoCodec) && profile.TonemapHdr && info.IsHdr)
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

    // The encoder converts every audio track, so the file is only compliant when *every* track already
    // matches the target codec; considering only the first track would skip a file whose other-language
    // tracks still force live transcoding — the exact thing pre-transcoding exists to prevent.
    private static bool AudioCodecNeedsWork(EncodingProfile profile, MediaProbeInfo info)
    {
        if (info.AudioStreams.Count > 0)
        {
            return info.AudioStreams.Any(s => !Same(s.Codec, profile.AudioCodec));
        }

        // A source with no audio at all is already compliant on this dimension: the encoder cannot add a
        // track, so re-transcoding would never make the (absent) audio codec match — flagging it forever
        // and re-transcoding a silent file on every sweep. Only a KNOWN codec that differs is work.
        return !string.IsNullOrEmpty(info.AudioCodec) && !Same(info.AudioCodec, profile.AudioCodec);
    }

    private static bool ExceedsChannels(EncodingProfile profile, MediaProbeInfo info)
    {
        if (IsCopy(profile.AudioCodec))
        {
            return false;
        }

        var cap = profile.ChannelPolicy switch
        {
            AudioChannelPolicy.CapStereo => 2,
            AudioChannelPolicy.Cap51 => 6,
            AudioChannelPolicy.CapCustom when profile.MaxAudioChannels > 0 => profile.MaxAudioChannels,
            _ => int.MaxValue
        };

        if (cap == int.MaxValue)
        {
            return false;
        }

        // The builder downmixes each track individually, so any single track over the cap is work.
        if (info.AudioStreams.Count > 0)
        {
            return info.AudioStreams.Any(s => s.Channels > cap);
        }

        return info.AudioChannels > cap;
    }

    private static bool ContainerMatches(string sourceContainer, string targetContainer)
    {
        sourceContainer ??= string.Empty;

        // An empty target container means "let the builder decide", and the builder (MuxerFor) defaults
        // an empty container to mp4. Comparing against "" literally never matched a real ffprobe
        // format_name, so the encoder's own mp4 output was flagged non-compliant forever; and a null
        // container would throw below. Normalise to the builder's default first.
        targetContainer = string.IsNullOrWhiteSpace(targetContainer) ? "mp4" : targetContainer;

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
