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
    // Codecs whose 10-bit profile is broadly supported by client hardware decoders, so keeping a 10-bit
    // source at 10 bits does not cost playability. H.264 is deliberately absent: its High10 profile is
    // not hardware-decodable on most clients, and playing everywhere is the entire point of
    // pre-transcoding — an admin who wants it anyway can select KeepSourceBitDepth explicitly.
    private static readonly string[] TenBitFriendlyCodecs = { "hevc", "h265", "av1", "vp9" };

    // The 10-bit formats an encoder may accept, in preference order. Never assumed — a candidate is only
    // used when the encoder's own "Supported pixel formats" list contains it.
    private static readonly string[] TenBitCandidates = { "yuv420p10le", "p010le" };

    // Builds the ordered ffmpeg argument list (suitable for ProcessStartInfo.ArgumentList).
    public static IReadOnlyList<string> BuildArguments(
        EncodingProfile profile,
        MediaProbeInfo source,
        IReadOnlyList<ResolutionPreset> presets,
        string inputPath,
        string outputPath,
        IReadOnlyList<string>? encoderPixelFormats = null,
        string? hardwareDecoder = null)
    {
        var args = new List<string> { "-y", "-hide_banner" };

        // -hwaccel is an INPUT option: it must precede the -i it applies to, which is why it is emitted
        // here rather than alongside the video settings it otherwise belongs with.
        AddHardwareDecoder(args, profile, hardwareDecoder);

        // Restrict the input to local-file protocols. The source is always a verified local library file,
        // so this changes nothing for legitimate input, but it makes the "never fetch a remote URL / read
        // an arbitrary file via concat:/subfile:/http:" property explicit and survivable across refactors
        // instead of relying solely on the caller's File.Exists gate.
        args.Add("-protocol_whitelist");
        args.Add("file,crypto,data");
        args.Add("-i");
        args.Add(inputPath);

        var mkvLike = IsMatroska(profile.Container);
        var mp4Like = IsMp4Like(profile.Container);

        // "0:V:0", not "0:v:0". Lowercase v matches every video stream INCLUDING an attached picture —
        // embedded cover art — while MediaProber deliberately skips attached_pic streams when it decides
        // what a file is. So the two could disagree about which stream is the video, and where they did,
        // the prober read the real h264 track and matched a rule on it while ffmpeg encoded the poster
        // thumbnail as the video and carried the full-length audio alongside it. That muxes and exits 0,
        // and OutputVerifier only compared duration — which the audio satisfies on its own — so under
        // Replace in place the original film was deleted and replaced by a still image with sound.
        //
        // Measured, because the ordering is what decides whether this can happen at all: it needs an
        // attached picture at a LOWER index than the real video, and neither common muxer produces one.
        // ffmpeg writes an mp4 attached picture as an iTunes covr atom, and mov_read_covr appends that
        // stream when it reaches udta — after the trak boxes — so the artwork is last; Matroska keeps
        // cover art as an attachment rather than a video track. On files from normal tools 0:v:0 and
        // 0:V:0 select the same stream. This is a guard against unusually ordered files, not a fix for
        // something that was happening routinely. Uppercase V excludes attached pictures.
        args.Add("-map");
        args.Add("0:V:0");
        args.Add("-map");
        args.Add("0:a?");
        // One decision about which source subtitle tracks travel, made here and reused when their output
        // codecs are chosen. They used to be decided twice — a wholesale "0:s?" for mkv against a
        // per-track loop for mp4 — and the codec assignment then indexed the SOURCE tracks while ffmpeg
        // numbered the OUTPUT ones. With a PGS track ahead of a subrip track in an mp4 profile, only the
        // subrip was mapped but "-c:s:0" was computed from the PGS track, came out "copy", and ffmpeg
        // refused to put subrip in mp4 — the whole encode failed on a subtitle nobody asked to keep.
        var subtitles = SubtitlesToCarry(profile.Container, source);
        var mapsEverySubtitle = mkvLike && !IsWebm(profile.Container) && source.SubtitleStreams.Count == 0;

        if (mapsEverySubtitle)
        {
            // The probe reported no per-stream subtitle detail, so there is nothing to select from; keep
            // the old wholesale mapping rather than dropping tracks that may well be there.
            args.Add("-map");
            args.Add("0:s?");
        }
        else
        {
            foreach (var index in subtitles)
            {
                args.Add("-map");
                args.Add("0:s:" + N(index));
            }
        }

        if (mkvLike && !IsWebm(profile.Container))
        {
            // Matroska carries embedded fonts, which ASS/SSA needs to render as authored. (webm holds no
            // attachments, and mp4's are not worth carrying.)
            args.Add("-map");
            args.Add("0:t?");
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

            // These used to be dropped without a word on a copy profile. A filter among them still is —
            // it cannot be applied to a stream that is not being re-encoded, and passing it would make
            // ffmpeg refuse the whole job — but everything else the admin configured is honoured.
            AddRawWithFilters(args, profile.ExtraVideoArgs, null);
        }
        else
        {
            args.Add("-c:v");
            args.Add(profile.VideoEncoder);

            if (!string.IsNullOrWhiteSpace(profile.Preset) && EncoderAcceptsPreset(profile.VideoEncoder, profile.Preset))
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

            // Emitted before ExtraVideoArgs so an admin can still override it outright.
            var pixelFormat = ChoosePixelFormat(profile, source, encoderPixelFormats);
            if (pixelFormat is not null)
            {
                args.Add("-pix_fmt");
                args.Add(pixelFormat);
            }

            AddRawWithFilters(args, profile.ExtraVideoArgs, filters);
        }

        if (filters.Count > 0)
        {
            args.Add("-vf");
            args.Add(string.Join(",", filters));
        }

        // ---- audio ----
        if (IsCopy(profile.AudioCodec))
        {
            AddCopyAudio(args, profile, source);
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
            args.Add(AudioContainerPolicy.EffectiveEncoder(profile));
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
        if (mkvLike && !IsWebm(profile.Container))
        {
            AddSubtitles(args, profile.Container, source, subtitles, mapsEverySubtitle);
        }
        else if ((mp4Like || IsWebm(profile.Container)) && source.SubtitleStreams.Any(s => IsTextSubtitle(s.Codec)))
        {
            // The tracks mapped above are all text: mov_text for mp4/mov, webvtt for webm.
            args.Add("-c:s");
            args.Add(IsWebm(profile.Container) ? "webvtt" : "mov_text");
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

    /// <summary>
    /// The pixel format to force on the re-encoded video, or <c>null</c> to let ffmpeg negotiate it.
    /// </summary>
    /// <param name="profile">The target profile.</param>
    /// <param name="source">The probed source.</param>
    /// <param name="encoderPixelFormats">
    /// The formats the chosen encoder advertises (from <c>ffmpeg -h encoder=…</c>). An empty or absent
    /// list means "unknown", and the safe 8-bit default is used rather than a guess.
    /// </param>
    /// <returns>The pixel format, or <c>null</c>.</returns>
    internal static string? ChoosePixelFormat(
        EncodingProfile profile,
        MediaProbeInfo source,
        IReadOnlyList<string>? encoderPixelFormats)
    {
        // Tone-mapping converts HDR to SDR and its filter chain already ends in yuv420p; the result is
        // 8-bit Rec.709 by construction, so anything else here would contradict the filter.
        if (profile.TonemapHdr && source.IsHdr)
        {
            return "yuv420p";
        }

        if (profile.PixelFormatMode == PixelFormatMode.ForceYuv420p)
        {
            return "yuv420p";
        }

        // BitDepth 0 means the probe could not tell; treat that as 8-bit rather than speculatively
        // asking the encoder for a depth the source may not have.
        if (source.BitDepth <= 8)
        {
            return "yuv420p";
        }

        var keep = profile.PixelFormatMode == PixelFormatMode.KeepSourceBitDepth
            // Auto keeps the depth for codecs whose 10-bit profile clients can actually decode, and
            // always for an HDR source that is NOT being tone-mapped: forcing that to 8 bits while its
            // HDR transfer tags survive is the one outcome that is never correct (banding).
            || (profile.PixelFormatMode == PixelFormatMode.Auto
                && (source.IsHdr || TenBitFriendlyCodecs.Contains(profile.VideoCodec, StringComparer.OrdinalIgnoreCase)));

        if (!keep || encoderPixelFormats is null || encoderPixelFormats.Count == 0)
        {
            return "yuv420p";
        }

        foreach (var candidate in TenBitCandidates)
        {
            if (encoderPixelFormats.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        // The encoder advertises formats but no 10-bit one (h264_qsv, for instance, offers only nv12).
        // Falling back to 8-bit keeps the encode working instead of failing on an unsupported format.
        return "yuv420p";
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

    /// <summary>
    /// Emits <c>-hwaccel</c> so ffmpeg decodes the source on the GPU instead of the CPU.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nothing is emitted for a <c>copy</c> video profile: no frames are decoded at all there, so the
    /// only thing initialising a hardware device could do is fail.
    /// </para>
    /// <para>
    /// Deliberately without <c>-hwaccel_output_format</c>. Naming an output format keeps decoded frames
    /// in GPU memory, which is faster again, but every filter this builder emits — the scale and tonemap
    /// chains — is a software filter that cannot read a hardware frame. Recent ffmpeg papers over that by
    /// inserting <c>hwdownload</c> into the graph itself: measured working on ffmpeg 9.0.1, videotoolbox
    /// plus a scale filter, output correct. That is version-dependent behaviour though, and the plugin
    /// runs against whatever binary the server happens to have. Leaving the option off asks for frames in
    /// system memory outright, so hardware decoding composes with resolution caps and tone-mapping on
    /// every version rather than on new ones. The copy back costs some of the win; depending on a filter
    /// ffmpeg may or may not insert for you could cost all of it.
    /// </para>
    /// <para>
    /// The value reaches ffmpeg through ArgumentList, so it is never shell-interpreted, but it is still
    /// restricted to the shape of a real method name — an admin-editable config string should not be
    /// able to smuggle a second ffmpeg option in via a space.
    /// </para>
    /// </remarks>
    /// <param name="args">The argument list being built.</param>
    /// <param name="profile">The target profile.</param>
    /// <param name="hardwareDecoder">The configured method, or null/empty for software decoding.</param>
    private static void AddHardwareDecoder(List<string> args, EncodingProfile profile, string? hardwareDecoder)
    {
        if (string.IsNullOrWhiteSpace(hardwareDecoder) || IsCopy(profile.VideoCodec))
        {
            return;
        }

        var method = hardwareDecoder.Trim().ToLowerInvariant();
        foreach (var c in method)
        {
            if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_'))
            {
                return;
            }
        }

        args.Add("-hwaccel");
        args.Add(method);
    }

    // A "copy" audio profile, honoured as far as the output container allows. Copy still means copy for
    // every track the container can store — which is all of them for Matroska, and the common case for
    // mp4 — and the wholesale "-c:a copy" is kept whenever that holds, so the emitted command is
    // unchanged for those profiles. Only when some track cannot be stored does this drop to per-track
    // specifiers and re-encode that one track, because the alternative is not "copy" but a muxer that
    // rejects the header and fails the entire job (an mkv source's TrueHD into mp4; an aac track into
    // webm).
    //
    // The channel cap is deliberately NOT applied to the tracks this is forced to re-encode. The admin
    // asked for copy, so the output should differ from the source only as far as the container compels;
    // silently downmixing 7.1 to stereo is a second, larger change nobody requested — and it would also
    // disagree with ProfileComplianceChecker, which ignores the channel cap on copy profiles.
    private static void AddCopyAudio(List<string> args, EncodingProfile profile, MediaProbeInfo source)
    {
        var storable = source.AudioStreams.Count == 0
            || source.AudioStreams.All(s => AudioContainerPolicy.CanStore(profile.Container, s.Codec));

        if (storable)
        {
            // Either the probe reported no per-stream detail (so there is nothing to select from, and a
            // blanket copy is the only answer available) or every track fits as it is.
            args.Add("-c:a");
            args.Add("copy");
            return;
        }

        var fallback = AudioContainerPolicy.FallbackEncoder(profile.Container);
        for (var i = 0; i < source.AudioStreams.Count; i++)
        {
            var idx = N(i);
            args.Add("-c:a:" + idx);
            if (AudioContainerPolicy.CanStore(profile.Container, source.AudioStreams[i].Codec))
            {
                args.Add("copy");
                continue;
            }

            args.Add(fallback);
            args.Add("-b:a:" + idx);
            args.Add(N(profile.AudioBitrateKbps) + "k");
        }
    }

    // Preserves every audio track (all languages). A track already in the target codec and within the
    // channel cap is copied verbatim (no quality loss); the rest are re-encoded, each downmixed only if
    // it individually exceeds the cap. Per-stream specifiers (:a:i) refer to the i-th mapped audio track.
    // NOTE: the channel-count option must also carry the audio type qualifier — "-ac:a:i". A bare
    // "-ac:i" is an output-stream-index specifier (stream 0 is the video, mapped first), so ffmpeg
    // silently ignores it and the downmix never happens — verified with real ffmpeg: "-ac:0 2" leaves a
    // 5.1 track at 6 channels, while "-ac:a:0 2" correctly yields stereo.
    //
    // The target is the codec the container can actually hold, which is the profile's own unless the two
    // are incompatible (aac into webm). Copying is then decided against that effective target, so a
    // source track is never copied into a container with no tag for it.
    private static void AddPerTrackAudio(List<string> args, EncodingProfile profile, MediaProbeInfo source)
    {
        var cap = ChannelCap(profile);
        var targetCodec = AudioContainerPolicy.EffectiveCodec(profile);
        var targetEncoder = AudioContainerPolicy.EffectiveEncoder(profile);

        for (var i = 0; i < source.AudioStreams.Count; i++)
        {
            var stream = source.AudioStreams[i];
            var idx = N(i);
            var withinCap = !cap.HasValue || stream.Channels <= cap.Value;

            if (Same(stream.Codec, targetCodec) && withinCap)
            {
                args.Add("-c:a:" + idx);
                args.Add("copy");
                continue;
            }

            args.Add("-c:a:" + idx);
            args.Add(targetEncoder);
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
    private static void AddSubtitles(
        List<string> args, string container, MediaProbeInfo source, List<int> subtitles, bool mapsEverySubtitle)
    {
        if (mapsEverySubtitle || subtitles.Count == 0)
        {
            // Either the probe gave no per-stream detail (so everything was mapped wholesale and copy is
            // the only safe blanket answer) or nothing is being carried at all.
            args.Add("-c:s");
            args.Add("copy");
            return;
        }

        // Indexed by OUTPUT position, which is the order the tracks were mapped in — not by source index.
        var textCodec = IsWebm(container) ? "webvtt" : "srt";
        for (var output = 0; output < subtitles.Count; output++)
        {
            var codec = source.SubtitleStreams[subtitles[output]].Codec;
            args.Add("-c:s:" + N(output));
            args.Add(CanStoreSubtitle(container, codec) ? "copy" : textCodec);
        }
    }

    /// <summary>
    /// The source subtitle tracks this container can actually end up holding: the ones it can store as
    /// they are, plus text ones it can hold after conversion.
    /// <para>
    /// An image subtitle the container cannot store is left behind, because there is nowhere for it to
    /// go — it cannot be converted to text. Matroska used to map every track wholesale and then route
    /// anything it did not recognise to srt, so an xsub track (bitmap subtitles from an AVI/DivX source,
    /// which the hard-coded list does not name) made ffmpeg abort the entire encode with "Subtitle
    /// encoding currently only possible from text to text or bitmap to bitmap".
    /// </para>
    /// </summary>
    private static List<int> SubtitlesToCarry(string container, MediaProbeInfo source)
    {
        var carried = new List<int>();
        for (var i = 0; i < source.SubtitleStreams.Count; i++)
        {
            var codec = source.SubtitleStreams[i].Codec;
            if (CanStoreSubtitle(container, codec) || IsTextSubtitle(codec))
            {
                carried.Add(i);
            }
        }

        return carried;
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

        // mp4/mov store timed text and nothing else. This used to fall through to the Matroska list, so a
        // subrip track was reported as storable and copied straight into an mp4 — which ffmpeg refuses
        // ("codec not currently supported in container"), failing the encode. Converting it to mov_text,
        // which is what saying "no" here produces, is the thing that actually works.
        if (IsMp4Like(container))
        {
            return Same(codec, "mov_text");
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

    // Which encoders can be handed -preset, and with what.
    //
    // VAAPI and VideoToolbox have no -preset at all and abort if given one (they use
    // -compression_level / -q:v). Neither do libvpx-vp9, libaom-av1 or librav1e, which express speed as
    // -cpu-used / -speed. libsvtav1 has one but it is an INTEGER — and the profile editor pre-fills the
    // field with the previous encoder's value, so switching libx264 -> libsvtav1 carried "medium" over
    // and every job for that profile died before encoding a frame with "ffmpeg exited with code 1".
    // Anything left over (nvenc, qsv, amf, libx264/x265) takes a named preset.
    private static bool EncoderAcceptsPreset(string encoder, string preset)
    {
        if (Has(encoder, "vaapi") || Has(encoder, "videotoolbox")
            || Has(encoder, "libvpx") || Has(encoder, "libaom") || Has(encoder, "librav1e"))
        {
            return false;
        }

        // SVT-AV1 numbers its presets. A named one is not a value it can use, so it is dropped rather
        // than passed on to fail the encode.
        if (Has(encoder, "svtav1") || Has(encoder, "svt_av1"))
        {
            return int.TryParse(preset, NumberStyles.Integer, CultureInfo.InvariantCulture, out _);
        }

        return true;
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
        args.AddRange(Tokenize(raw));
    }

    /// <summary>
    /// Splits an admin-typed argument string the way a shell would, and lifts any video filter it
    /// contains into <paramref name="filters"/> instead of emitting it.
    /// <para>
    /// ffmpeg keeps only the LAST <c>-vf</c> for a stream, and the builder emits its own after the extra
    /// args — so an admin who added <c>-vf hqdn3d</c> to a profile that also caps the resolution had
    /// their filter silently discarded, with only an ffmpeg warning on a stream nothing reads on a
    /// successful job. Merging the two chains keeps both, which is what "extra" was meant to mean.
    /// </para>
    /// </summary>
    /// <param name="args">The argument list to append the non-filter tokens to.</param>
    /// <param name="raw">The admin's argument string.</param>
    /// <param name="filters">The filter chain being built, or <c>null</c> to drop filters entirely —
    /// which is the copy case, where a filter cannot be applied to a stream that is not re-encoded.</param>
    private static void AddRawWithFilters(List<string> args, string raw, List<string>? filters)
    {
        var tokens = Tokenize(raw);
        for (var i = 0; i < tokens.Count; i++)
        {
            if ((Same(tokens[i], "-vf") || Same(tokens[i], "-filter:v")) && i + 1 < tokens.Count)
            {
                filters?.Add(tokens[i + 1]);
                i++;
                continue;
            }

            args.Add(tokens[i]);
        }
    }

    private static List<string> Tokenize(string raw)
    {
        var tokens = new List<string>();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return tokens;
        }

        // Split on spaces but keep double-quoted runs together, so an advanced arg like
        // -metadata title="My Movie" becomes two tokens (-metadata, title=My Movie) instead of three
        // with stray quote characters. Each resulting token is passed verbatim via ArgumentList.
        var current = new StringBuilder();
        var inQuotes = false;
        foreach (var c in raw)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (c == ' ' && !inQuotes)
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }

    private static string N(int value)
    {
        return value.ToString(CultureInfo.InvariantCulture);
    }
}
