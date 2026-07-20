using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.PreTranscode.Configuration;
using Jellyfin.Plugin.PreTranscode.Media;

namespace Jellyfin.Plugin.PreTranscode.Encoding;

/// <summary>
/// Pure builder that turns a resolved <see cref="EncodingProfile"/> plus a probed source into the
/// exact ffmpeg argument list. No process is executed here, so the mapping is fully unit-testable.
/// </summary>
internal static class FfmpegCommandBuilder
{
    // Builds the ordered ffmpeg argument list (suitable for ProcessStartInfo.ArgumentList).
    public static IReadOnlyList<string> BuildArguments(
        EncodingProfile profile,
        MediaProbeInfo source,
        IReadOnlyList<ResolutionPreset> presets,
        string inputPath,
        string outputPath)
    {
        // Restrict the input to local-file protocols. The source is always a verified local library file,
        // so this changes nothing for legitimate input, but it makes the "never fetch a remote URL / read
        // an arbitrary file via concat:/subfile:/http:" property explicit and survivable across refactors
        // instead of relying solely on the caller's File.Exists gate.
        var args = new List<string> { "-y", "-hide_banner", "-protocol_whitelist", "file,crypto,data", "-i", inputPath };

        var mkvLike = IsMatroska(profile.Container);
        var mp4Like = IsMp4Like(profile.Container);

        args.Add("-map");
        args.Add("0:v:0");
        args.Add("-map");
        args.Add("0:a?");
        if (mkvLike)
        {
            // Matroska can hold every subtitle format (text and image) plus attachments, so carry all
            // subtitle tracks and any embedded fonts (needed for ASS/SSA to render) across losslessly.
            args.Add("-map");
            args.Add("0:s?");
            args.Add("-map");
            args.Add("0:t?");
        }
        else if (mp4Like)
        {
            // mp4/mov can store text subtitles (as mov_text) but not image subtitles (PGS/VOBSUB) — the
            // muxer rejects a bitmap track outright. Map each text subtitle track individually and leave
            // image tracks out, so subtitles survive the transcode instead of being silently dropped.
            for (var i = 0; i < source.SubtitleStreams.Count; i++)
            {
                if (IsTextSubtitle(source.SubtitleStreams[i].Codec))
                {
                    args.Add("-map");
                    args.Add("0:s:" + N(i));
                }
            }
        }

        args.Add("-map_metadata");
        args.Add("0");
        args.Add("-map_chapters");
        args.Add("0");

        // ---- video ----
        var filters = new List<string>();
        if (IsCopy(profile.VideoCodec))
        {
            args.Add("-c:v");
            args.Add("copy");
        }
        else
        {
            args.Add("-c:v");
            args.Add(profile.VideoEncoder);

            if (!string.IsNullOrWhiteSpace(profile.Preset) && EncoderAcceptsPreset(profile.VideoEncoder))
            {
                args.Add("-preset");
                args.Add(profile.Preset.Trim());
            }

            AddQuality(args, profile);

            if (profile.TonemapHdr && source.IsHdr)
            {
                filters.Add(BuildTonemapChain(profile.TonemapAlgorithm));
            }

            var scale = ResolutionCalculator.BuildScaleFilter(profile, source, presets);
            if (scale is not null)
            {
                filters.Add(scale);
            }

            args.Add("-pix_fmt");
            args.Add("yuv420p");
            AddRaw(args, profile.ExtraVideoArgs);
        }

        if (filters.Count > 0)
        {
            args.Add("-vf");
            args.Add(string.Join(",", filters));
        }

        // ---- audio ----
        if (IsCopy(profile.AudioCodec))
        {
            args.Add("-c:a");
            args.Add("copy");
        }
        else if (source.AudioStreams.Count > 0)
        {
            AddPerTrackAudio(args, profile, source);
        }
        else
        {
            // Fallback when the probe did not enumerate per-stream info: apply the target codec to
            // every audio track at once (mapped via 0:a?), downmixing from the first stream's layout.
            args.Add("-c:a");
            args.Add(profile.AudioEncoder);
            args.Add("-b:a");
            args.Add(N(profile.AudioBitrateKbps) + "k");

            var cap = ChannelCap(profile);
            if (cap.HasValue && source.AudioChannels > cap.Value)
            {
                args.Add("-ac");
                args.Add(N(cap.Value));
            }
        }

        // ---- subtitles ----
        if (mkvLike)
        {
            AddSubtitles(args, profile.Container, source);
        }
        else if (mp4Like && source.SubtitleStreams.Any(s => IsTextSubtitle(s.Codec)))
        {
            // The tracks mapped above are all text; mov_text is the subtitle codec mp4/mov carry.
            args.Add("-c:s");
            args.Add("mov_text");
        }

        AddRaw(args, profile.ExtraOutputArgs);

        // ---- container ----
        args.Add("-f");
        args.Add(MuxerFor(profile.Container));
        if (IsMp4Like(profile.Container))
        {
            args.Add("-movflags");
            args.Add("+faststart");
        }

        args.Add(outputPath);
        return args;
    }

    // Renders an argument list as a single, log-friendly command line (tokens with spaces are quoted).
    public static string ToCommandLine(IReadOnlyList<string> arguments)
    {
        var sb = new StringBuilder("ffmpeg");
        foreach (var arg in arguments)
        {
            sb.Append(' ');
            if (arg.Contains(' ', StringComparison.Ordinal))
            {
                sb.Append('"').Append(arg).Append('"');
            }
            else
            {
                sb.Append(arg);
            }
        }

        return sb.ToString();
    }

    // Preserves every audio track (all languages). A track already in the target codec and within the
    // channel cap is copied verbatim (no quality loss); the rest are re-encoded, each downmixed only if
    // it individually exceeds the cap. Per-stream specifiers (:a:i) refer to the i-th mapped audio track.
    // NOTE: the channel-count option must also carry the audio type qualifier — "-ac:a:i". A bare
    // "-ac:i" is an output-stream-index specifier (stream 0 is the video, mapped first), so ffmpeg
    // silently ignores it and the downmix never happens — verified with real ffmpeg: "-ac:0 2" leaves a
    // 5.1 track at 6 channels, while "-ac:a:0 2" correctly yields stereo.
    private static void AddPerTrackAudio(List<string> args, EncodingProfile profile, MediaProbeInfo source)
    {
        var cap = ChannelCap(profile);
        for (var i = 0; i < source.AudioStreams.Count; i++)
        {
            var stream = source.AudioStreams[i];
            var idx = N(i);
            var withinCap = !cap.HasValue || stream.Channels <= cap.Value;

            if (Same(stream.Codec, profile.AudioCodec) && withinCap)
            {
                args.Add("-c:a:" + idx);
                args.Add("copy");
                continue;
            }

            args.Add("-c:a:" + idx);
            args.Add(profile.AudioEncoder);
            args.Add("-b:a:" + idx);
            args.Add(N(profile.AudioBitrateKbps) + "k");
            if (cap.HasValue && stream.Channels > cap.Value)
            {
                args.Add("-ac:a:" + idx);
                args.Add(N(cap.Value));
            }
        }
    }

    // A Matroska output cannot hold every subtitle codec. An mp4 source's mov_text tracks in particular
    // make the muxer reject the header outright, which kills the whole transcode seconds in, so a track
    // the container cannot store is converted to a text format it can rather than copied.
    private static void AddSubtitles(List<string> args, string container, MediaProbeInfo source)
    {
        if (source.SubtitleStreams.Count == 0)
        {
            // No per-stream info from the probe: nothing is mapped in practice, and copy stays correct.
            args.Add("-c:s");
            args.Add("copy");
            return;
        }

        var textCodec = IsWebm(container) ? "webvtt" : "srt";
        for (var i = 0; i < source.SubtitleStreams.Count; i++)
        {
            args.Add("-c:s:" + N(i));
            args.Add(CanStoreSubtitle(container, source.SubtitleStreams[i].Codec) ? "copy" : textCodec);
        }
    }

    // Text-based subtitle codecs, which can be transcoded to another text format (srt, webvtt, mov_text).
    // Image subtitles (PGS/VOBSUB/DVB) cannot, so they must not be routed to a text-only container.
    private static bool IsTextSubtitle(string codec)
    {
        return (codec ?? string.Empty).ToLowerInvariant() switch
        {
            "subrip" or "srt" or "ass" or "ssa" or "webvtt" or "mov_text" or "text"
                or "subviewer" or "microdvd" or "sami" or "realtext" or "stl" => true,
            _ => false
        };
    }

    private static bool CanStoreSubtitle(string container, string codec)
    {
        if (IsWebm(container))
        {
            return Same(codec, "webvtt");
        }

        return codec.ToLowerInvariant() switch
        {
            "subrip" or "srt" or "text" or "ass" or "ssa" or "webvtt" => true,
            "hdmv_pgs_subtitle" or "dvd_subtitle" or "dvb_subtitle" or "hdmv_text_subtitle" => true,
            _ => false
        };
    }

    private static void AddQuality(List<string> args, EncodingProfile profile)
    {
        if (profile.VideoQualityMode == QualityMode.Crf)
        {
            args.Add(CrfFlagFor(profile.VideoEncoder));
            args.Add(N(profile.Crf));
            return;
        }

        args.Add("-b:v");
        args.Add(N(profile.VideoBitrateKbps) + "k");
        if (profile.VideoMaxBitrateKbps > 0)
        {
            args.Add("-maxrate");
            args.Add(N(profile.VideoMaxBitrateKbps) + "k");
            args.Add("-bufsize");
            args.Add(N(profile.VideoMaxBitrateKbps * 2) + "k");
        }
    }

    // The VAAPI and VideoToolbox encoders have no -preset option and abort if given one (they use
    // -compression_level / -q:v instead). nvenc, qsv, amf and the software encoders (libx264/x265/svtav1)
    // all accept -preset — verified against the bundled ffmpeg for nvenc/qsv/amf — so only the two
    // preset-less families are excluded.
    private static bool EncoderAcceptsPreset(string encoder)
    {
        return !Has(encoder, "vaapi") && !Has(encoder, "videotoolbox");
    }

    // The constant-quality flag is encoder-specific; there is no universal ffmpeg option.
    private static string CrfFlagFor(string encoder)
    {
        if (Has(encoder, "nvenc"))
        {
            return "-cq";
        }

        if (Has(encoder, "qsv"))
        {
            return "-global_quality";
        }

        if (Has(encoder, "vaapi"))
        {
            return "-qp";
        }

        if (Has(encoder, "videotoolbox"))
        {
            return "-q:v";
        }

        if (Has(encoder, "amf"))
        {
            return "-qp_i";
        }

        // libx264 / libx265 / libsvtav1 / libaom-av1 / libvpx-vp9 ...
        return "-crf";
    }

    private static string BuildTonemapChain(string algorithm)
    {
        var algo = SanitizeTonemapAlgorithm(algorithm);
        return "zscale=t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,"
            + "tonemap=tonemap=" + algo + ":desat=0,"
            + "zscale=t=bt709:m=bt709:r=tv,format=yuv420p";
    }

    // The algorithm is embedded verbatim in the filtergraph, so restrict it to bare alphanumerics: a
    // value like "hable,movie=/etc/passwd[o]" would otherwise inject an arbitrary ffmpeg filter. Every
    // real tonemap algorithm name (hable, mobius, reinhard, bt2390, …) is lowercase alphanumeric; an
    // invalid value collapses to a harmless unknown name that ffmpeg rejects cleanly.
    private static string SanitizeTonemapAlgorithm(string algorithm)
    {
        var cleaned = new StringBuilder((algorithm ?? string.Empty).Length);
        foreach (var c in (algorithm ?? string.Empty).ToLowerInvariant())
        {
            if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9'))
            {
                cleaned.Append(c);
            }
        }

        return cleaned.Length == 0 ? "hable" : cleaned.ToString();
    }

    private static int? ChannelCap(EncodingProfile profile)
    {
        return profile.ChannelPolicy switch
        {
            AudioChannelPolicy.CapStereo => 2,
            AudioChannelPolicy.Cap51 => 6,
            AudioChannelPolicy.CapCustom => profile.MaxAudioChannels > 0 ? profile.MaxAudioChannels : null,
            _ => null
        };
    }

    private static string MuxerFor(string container)
    {
        return container.ToLowerInvariant() switch
        {
            "mkv" => "matroska",
            "" => "mp4",
            _ => container
        };
    }

    private static bool IsMatroska(string container)
    {
        var c = container.ToLowerInvariant();
        return c is "matroska" or "mkv" or "webm";
    }

    private static bool IsWebm(string container)
    {
        return string.Equals(container, "webm", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMp4Like(string container)
    {
        var c = container.ToLowerInvariant();
        return c is "mp4" or "mov" or "m4v" or "ipod";
    }

    private static bool IsCopy(string codec)
    {
        return string.Equals(codec, "copy", StringComparison.OrdinalIgnoreCase);
    }

    private static bool Same(string a, string b)
    {
        return string.Equals(a ?? string.Empty, b ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    private static bool Has(string value, string token)
    {
        return value.Contains(token, StringComparison.OrdinalIgnoreCase);
    }

    private static void AddRaw(List<string> args, string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return;
        }

        foreach (var token in raw.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            args.Add(token);
        }
    }

    private static string N(int value)
    {
        return value.ToString(CultureInfo.InvariantCulture);
    }
}
