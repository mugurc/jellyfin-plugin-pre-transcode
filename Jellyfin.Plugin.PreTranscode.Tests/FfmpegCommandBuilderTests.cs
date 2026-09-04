using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.PreTranscode.Configuration;
using Jellyfin.Plugin.PreTranscode.Encoding;
using Jellyfin.Plugin.PreTranscode.Media;

namespace Jellyfin.Plugin.PreTranscode.Tests;

public class FfmpegCommandBuilderTests
{
    private static readonly IReadOnlyList<ResolutionPreset> Presets = new[]
    {
        new ResolutionPreset { Id = "1080p", Name = "1080p", Width = 1920, Height = 1080 }
    };

    private static EncodingProfile BaseProfile()
    {
        return new EncodingProfile
        {
            VideoCodec = "h264", VideoEncoder = "libx264", VideoQualityMode = QualityMode.Crf, Crf = 21,
            Preset = "medium", ResolutionMode = ResolutionMode.Unchanged,
            AudioCodec = "aac", AudioEncoder = "aac", AudioBitrateKbps = 256,
            ChannelPolicy = AudioChannelPolicy.Unchanged, Container = "mp4", TonemapHdr = false
        };
    }

    private static EncodingProfile WebmProfile()
    {
        var p = BaseProfile();
        p.VideoCodec = "vp9";
        p.VideoEncoder = "libvpx-vp9";
        p.Container = "webm";
        return p;
    }

    private static MediaProbeInfo Source(int w = 1920, int h = 1080, int ch = 2, bool hdr = false)
    {
        return new MediaProbeInfo { VideoCodec = "hevc", Width = w, Height = h, AudioChannels = ch, IsHdr = hdr };
    }

    private static string Build(EncodingProfile p, MediaProbeInfo s)
    {
        return string.Join(" ", FfmpegCommandBuilder.BuildArguments(p, s, Presets, "/in.mkv", "/out.mp4"));
    }

    [Fact]
    public void Input_RestrictedToLocalFileProtocols_BeforeInput()
    {
        var args = FfmpegCommandBuilder.BuildArguments(BaseProfile(), Source(), Presets, "/in.mkv", "/out.mp4").ToList();
        var w = args.IndexOf("-protocol_whitelist");
        var i = args.IndexOf("-i");
        Assert.True(w >= 0, "the input must carry a protocol whitelist");
        Assert.Equal("file,crypto,data", args[w + 1]);
        Assert.True(w < i, "the whitelist must precede -i to apply to that input");
    }

    [Fact]
    public void TonemapAlgorithm_IsSanitized_NoFilterInjection()
    {
        var p = BaseProfile();
        p.TonemapHdr = true;
        p.TonemapAlgorithm = "hable,movie=/etc/passwd[o]";
        var cmd = Build(p, Source(hdr: true));

        // The malicious value must not survive as an injectable ffmpeg filter.
        Assert.DoesNotContain("movie=", cmd);
        Assert.DoesNotContain("/etc/passwd", cmd);
        Assert.Contains("tonemap=tonemap=hablemovieetcpasswdo:desat=0", cmd);
    }

    [Fact]
    public void Crf_SoftwareEncoder_ProducesExpectedCoreArgs()
    {
        var cmd = Build(BaseProfile(), Source());
        Assert.Contains("-c:v libx264", cmd);
        Assert.Contains("-preset medium", cmd);
        Assert.Contains("-crf 21", cmd);
        Assert.Contains("-pix_fmt yuv420p", cmd);
        Assert.Contains("-c:a aac", cmd);
        Assert.Contains("-b:a 256k", cmd);
        Assert.Contains("-f mp4", cmd);
        Assert.Contains("-movflags +faststart", cmd);
        Assert.Contains("/in.mkv", cmd);
        Assert.Contains("/out.mp4", cmd);
    }

    [Fact]
    public void BitrateMode_EmitsBitrateAndMaxrate()
    {
        var p = BaseProfile();
        p.VideoQualityMode = QualityMode.Bitrate;
        p.VideoBitrateKbps = 6000;
        p.VideoMaxBitrateKbps = 8000;
        var cmd = Build(p, Source());
        Assert.Contains("-b:v 6000k", cmd);
        Assert.Contains("-maxrate 8000k", cmd);
        Assert.Contains("-bufsize 16000k", cmd);
        Assert.DoesNotContain("-crf", cmd);
    }

    [Fact]
    public void NvencEncoder_UsesCqFlag()
    {
        var p = BaseProfile();
        p.VideoEncoder = "h264_nvenc";
        Assert.Contains("-cq 21", Build(p, Source()));
    }

    [Fact]
    public void VaapiEncoder_OmitsPreset()
    {
        // h264_vaapi/hevc_vaapi (and videotoolbox) have no -preset option and abort if given one. nvenc,
        // qsv, amf and software encoders keep it.
        var p = BaseProfile();
        p.VideoEncoder = "h264_vaapi";
        p.Preset = "medium";
        Assert.DoesNotContain("-preset", Build(p, Source()));

        p.VideoEncoder = "h264_nvenc";
        Assert.Contains("-preset medium", Build(p, Source()));
    }

    [Fact]
    public void CapHeight_AddsScaleFilter_OnlyWhenExceeding()
    {
        var p = BaseProfile();
        p.ResolutionMode = ResolutionMode.CapHeight;
        p.MaxHeight = 1080;
        Assert.Contains("-vf scale=-2:1080", Build(p, Source(3840, 2160)));
        Assert.DoesNotContain("scale=", Build(p, Source(1280, 720)));
    }

    [Fact]
    public void OddResolutionCap_RoundedDownToEven()
    {
        // yuv420p (4:2:0) output requires even dimensions. An odd cap must be rounded down to even,
        // otherwise ffmpeg aborts the whole encode with "width not divisible by 2".
        var p = BaseProfile();
        p.ResolutionMode = ResolutionMode.CapWidth;
        p.MaxWidth = 1281;
        var cmd = Build(p, Source(1920, 1080));
        Assert.Contains("-vf scale=1280:-2", cmd);
        Assert.DoesNotContain("scale=1281", cmd);
    }

    [Fact]
    public void CopyVideo_EmitsCopy_NoQuality()
    {
        var p = BaseProfile();
        p.VideoCodec = "copy";
        var cmd = Build(p, Source());
        Assert.Contains("-c:v copy", cmd);
        Assert.DoesNotContain("-crf", cmd);
        Assert.DoesNotContain("-pix_fmt", cmd);
    }

    [Fact]
    public void CopyAudio_EmitsCopy()
    {
        var p = BaseProfile();
        p.AudioCodec = "copy";
        Assert.Contains("-c:a copy", Build(p, Source()));
    }

    [Fact]
    public void ChannelCap_DownmixesOnlyWhenSourceExceeds()
    {
        var p = BaseProfile();
        p.ChannelPolicy = AudioChannelPolicy.CapStereo;
        Assert.Contains("-ac 2", Build(p, Source(ch: 6)));
        Assert.DoesNotContain("-ac", Build(p, Source(ch: 2)));
    }

    [Fact]
    public void Tonemap_OnlyForHdrSource()
    {
        var p = BaseProfile();
        p.TonemapHdr = true;
        p.TonemapAlgorithm = "hable";
        Assert.Contains("tonemap=tonemap=hable", Build(p, Source(hdr: true)));
        Assert.DoesNotContain("tonemap", Build(p, Source(hdr: false)));
    }

    [Fact]
    public void MatroskaContainer_MapsSubtitlesAttachmentsAndUsesMatroskaMuxer()
    {
        var p = BaseProfile();
        p.Container = "mkv";
        var cmd = Build(p, Source());
        Assert.Contains("-f matroska", cmd);
        Assert.Contains("0:s?", cmd);
        Assert.Contains("0:t?", cmd);
        Assert.Contains("-c:s copy", cmd);
        Assert.DoesNotContain("-movflags", cmd);
    }

    [Fact]
    public void Matroska_ConvertsMovTextSubtitles_WhichTheMuxerCannotStore()
    {
        // Regression: copying an mp4 source's mov_text track into Matroska makes the muxer reject the
        // header ("Subtitle codec 94213 is not supported") and the whole transcode fails seconds in.
        var p = BaseProfile();
        p.Container = "mkv";
        var s = Source();
        s.SubtitleStreams = new[] { new SubtitleStreamInfo { Codec = "mov_text", Language = "eng" } };
        var cmd = Build(p, s);
        Assert.Contains("-c:s:0 srt", cmd);
        Assert.DoesNotContain("-c:s copy", cmd);
    }

    [Fact]
    public void Matroska_CopiesStorableSubtitles_ConvertsOnlyTheRest()
    {
        var p = BaseProfile();
        p.Container = "mkv";
        var s = Source();
        s.SubtitleStreams = new[]
        {
            new SubtitleStreamInfo { Codec = "subrip" },            // -> copy
            new SubtitleStreamInfo { Codec = "mov_text" },           // -> convert
            new SubtitleStreamInfo { Codec = "hdmv_pgs_subtitle" },  // -> copy (bitmap, storable)
            new SubtitleStreamInfo { Codec = "ass" },                // -> copy
        };
        var cmd = Build(p, s);
        Assert.Contains("-c:s:0 copy", cmd);
        Assert.Contains("-c:s:1 srt", cmd);
        Assert.Contains("-c:s:2 copy", cmd);
        Assert.Contains("-c:s:3 copy", cmd);
    }

    [Fact]
    public void Webm_KeepsTextSubtitlesAsWebvtt_DropsImageSubtitles()
    {
        var p = BaseProfile();
        p.Container = "webm";
        var s = Source();
        s.SubtitleStreams = new[]
        {
            new SubtitleStreamInfo { Codec = "subrip" },            // text -> mapped, converted
            new SubtitleStreamInfo { Codec = "hdmv_pgs_subtitle" }, // image -> dropped (webm can't store it)
        };
        var cmd = Build(p, s);
        Assert.Contains("-map 0:s:0", cmd);
        Assert.DoesNotContain("-map 0:s:1", cmd);
        Assert.Contains("-c:s webvtt", cmd);
    }

    [Fact]
    public void Mp4Container_DoesNotMapAttachments()
    {
        var cmd = Build(BaseProfile(), Source());
        Assert.DoesNotContain("0:t?", cmd);
    }

    [Fact]
    public void Mp4Container_KeepsTextSubtitlesAsMovText()
    {
        // Regression: mp4/mov previously mapped no subtitles at all, silently dropping them. Text tracks
        // must be carried across as mov_text (verified with real ffmpeg) instead of lost.
        var p = BaseProfile(); // Container = mp4
        var s = Source();
        s.SubtitleStreams = new[]
        {
            new SubtitleStreamInfo { Codec = "subrip" },
            new SubtitleStreamInfo { Codec = "ass" },
        };
        var cmd = Build(p, s);
        Assert.Contains("-map 0:s:0", cmd);
        Assert.Contains("-map 0:s:1", cmd);
        Assert.Contains("-c:s mov_text", cmd);
    }

    [Fact]
    public void Mp4Container_DropsImageSubtitles()
    {
        // mp4/mov cannot store bitmap subtitles (PGS/VOBSUB) and mov_text cannot encode them, so an
        // image-only source maps no subtitle track (dropping it) rather than failing the whole encode.
        var p = BaseProfile(); // Container = mp4
        var s = Source();
        s.SubtitleStreams = new[] { new SubtitleStreamInfo { Codec = "hdmv_pgs_subtitle" } };
        var cmd = Build(p, s);
        Assert.DoesNotContain("-map 0:s", cmd);
        Assert.DoesNotContain("mov_text", cmd);
    }

    [Fact]
    public void MultiAudio_CopiesTracksAlreadyInTargetCodec_ReEncodesTheRest()
    {
        var p = BaseProfile(); // target audio codec = aac
        var s = Source();
        s.AudioStreams = new[]
        {
            new AudioStreamInfo { Codec = "ac3", Channels = 6, Language = "eng" }, // -> re-encode
            new AudioStreamInfo { Codec = "aac", Channels = 2, Language = "tur" }, // -> copy verbatim
        };
        var cmd = Build(p, s);
        Assert.Contains("-c:a:0 aac", cmd);
        Assert.Contains("-b:a:0 256k", cmd);
        Assert.Contains("-c:a:1 copy", cmd);
        Assert.DoesNotContain("-b:a:1", cmd);
    }

    [Fact]
    public void MultiAudio_ChannelCapAppliesPerTrack()
    {
        var p = BaseProfile();
        p.ChannelPolicy = AudioChannelPolicy.CapStereo; // cap = 2
        var s = Source();
        s.AudioStreams = new[]
        {
            new AudioStreamInfo { Codec = "aac", Channels = 6 }, // aac but over cap -> re-encode + downmix
            new AudioStreamInfo { Codec = "aac", Channels = 2 }, // aac within cap -> copy, no downmix
        };
        var cmd = Build(p, s);
        Assert.Contains("-c:a:0 aac", cmd);
        // Must carry the audio type qualifier (-ac:a:0). A bare -ac:0 targets output stream 0 (the
        // video) and is silently ignored by ffmpeg, so the downmix would never apply.
        Assert.Contains("-ac:a:0 2", cmd);
        Assert.DoesNotContain("-ac:0", cmd);
        Assert.Contains("-c:a:1 copy", cmd);
        Assert.DoesNotContain("-ac:a:1", cmd);
    }

    [Fact]
    public void CopyAudioProfile_StillGlobalCopy_EvenWithPerStreamInfo()
    {
        var p = BaseProfile();
        p.AudioCodec = "copy";
        var s = Source();
        s.AudioStreams = new[]
        {
            new AudioStreamInfo { Codec = "ac3", Channels = 6 },
            new AudioStreamInfo { Codec = "dts", Channels = 8 },
        };
        var cmd = Build(p, s);
        Assert.Contains("-c:a copy", cmd);
        Assert.DoesNotContain("-c:a:0", cmd);
    }

    [Fact]
    public void UsePreset_DownscalesWithAspectPreserved()
    {
        var p = BaseProfile();
        p.ResolutionMode = ResolutionMode.UsePreset;
        p.ResolutionPresetId = "1080p";
        var cmd = Build(p, Source(3840, 2160));
        Assert.Contains("force_original_aspect_ratio=decrease", cmd);
        Assert.Contains("force_divisible_by=2", cmd);
    }

    // Cover art is a "video" stream with attached_pic=1, and in mp4/mov it is routinely stream #0:0.
    // MediaProber skips those when deciding what the file is, so mapping 0:v:0 made ffmpeg encode the
    // poster thumbnail while the prober had matched a rule on the real video track — and with only a
    // duration check standing between that and Replace-in-place, the film was replaced by a still image.
    [Fact]
    public void VideoMapExcludesAttachedCoverArt()
    {
        var args = FfmpegCommandBuilder.BuildArguments(
            new EncodingProfile { VideoEncoder = "libx265", AudioCodec = "copy", Container = "matroska" },
            new MediaProbeInfo { VideoCodec = "h264", Width = 1920, Height = 1080, Container = "mov,mp4" },
            new List<ResolutionPreset>(),
            "/media/Movie.mp4",
            "/tmp/out.mkv",
            Array.Empty<string>());

        Assert.Contains("0:V:0", args, StringComparer.Ordinal);
        Assert.DoesNotContain("0:v:0", args, StringComparer.Ordinal);
    }

    // libvpx-vp9, libaom-av1 and librav1e have no -preset at all; libsvtav1 has one but it is an integer,
    // and the profile editor pre-fills the field with the previous encoder's value, so switching
    // libx264 -> libsvtav1 carried "medium" over and every job died before encoding a frame.
    [Theory]
    [InlineData("libvpx-vp9", "good", false)]
    [InlineData("libaom-av1", "8", false)]
    [InlineData("librav1e", "6", false)]
    [InlineData("libsvtav1", "medium", false)]
    [InlineData("libsvtav1", "8", true)]
    [InlineData("libx265", "slow", true)]
    [InlineData("hevc_nvenc", "p4", true)]
    public void PresetIsOnlyPassedToEncodersThatTakeIt(string encoder, string preset, bool expected)
    {
        var args = FfmpegCommandBuilder.BuildArguments(
            new EncodingProfile { VideoEncoder = encoder, Preset = preset, AudioCodec = "copy", Container = "matroska" },
            new MediaProbeInfo { VideoCodec = "h264", Width = 1920, Height = 1080 },
            new List<ResolutionPreset>(),
            "/media/Movie.mkv",
            "/tmp/out.mkv",
            Array.Empty<string>());

        Assert.Equal(expected, args.Contains("-preset", StringComparer.Ordinal));
    }

    // Matroska used to map every subtitle track wholesale and then route anything its hard-coded list did
    // not name to srt. xsub — bitmap subtitles from an AVI/DivX source — is exactly that, and ffmpeg
    // aborts the whole encode with "only possible from text to text or bitmap to bitmap".
    [Fact]
    public void ImageSubtitleTheContainerCannotStore_IsLeftBehindRatherThanConvertedToText()
    {
        var source = new MediaProbeInfo
        {
            VideoCodec = "h264", Width = 1920, Height = 1080,
            SubtitleStreams = new List<SubtitleStreamInfo>
            {
                new SubtitleStreamInfo { Codec = "xsub" },
                new SubtitleStreamInfo { Codec = "subrip" }
            }
        };

        var args = FfmpegCommandBuilder.BuildArguments(
            new EncodingProfile { VideoEncoder = "libx265", AudioCodec = "copy", Container = "matroska" },
            source,
            new List<ResolutionPreset>(),
            "/media/Movie.avi",
            "/tmp/out.mkv",
            Array.Empty<string>());

        // Only the subrip track travels, and it is the output's subtitle stream 0.
        Assert.Contains("0:s:1", args, StringComparer.Ordinal);
        Assert.DoesNotContain("0:s:0", args, StringComparer.Ordinal);
        Assert.Contains("-c:s:0", args, StringComparer.Ordinal);
        Assert.DoesNotContain("-c:s:1", args, StringComparer.Ordinal);
    }

    // mp4 holds timed text and nothing else. The image track is left behind — there is nowhere for it to
    // go — and the text track that does travel is converted rather than copied.
    [Fact]
    public void Mp4CarriesOnlyTheTextSubtitle_AndConvertsIt()
    {
        var source = new MediaProbeInfo
        {
            VideoCodec = "h264", Width = 1920, Height = 1080,
            SubtitleStreams = new List<SubtitleStreamInfo>
            {
                new SubtitleStreamInfo { Codec = "hdmv_pgs_subtitle" },
                new SubtitleStreamInfo { Codec = "subrip" }
            }
        };

        var args = FfmpegCommandBuilder.BuildArguments(
            new EncodingProfile { VideoEncoder = "libx264", AudioCodec = "copy", Container = "mp4" },
            source,
            new List<ResolutionPreset>(),
            "/media/Movie.mkv",
            "/tmp/out.mp4",
            Array.Empty<string>());

        Assert.Contains("0:s:1", args, StringComparer.Ordinal);
        Assert.DoesNotContain("0:s:0", args, StringComparer.Ordinal);
        Assert.Contains("mov_text", args, StringComparer.Ordinal);
    }

    // ffmpeg keeps only the LAST -vf for a stream and the builder emitted its own after the extra args, so
    // an admin who added a filter to a profile that also caps the resolution had it silently discarded.
    [Fact]
    public void AdminVideoFilterIsMergedWithTheBuildersOwn()
    {
        var args = FfmpegCommandBuilder.BuildArguments(
            new EncodingProfile
            {
                VideoEncoder = "libx264", AudioCodec = "copy", Container = "matroska",
                ResolutionMode = ResolutionMode.CapHeight, MaxHeight = 1080,
                ExtraVideoArgs = "-vf hqdn3d"
            },
            new MediaProbeInfo { VideoCodec = "h264", Width = 3840, Height = 2160 },
            new List<ResolutionPreset>(),
            "/media/Movie.mkv",
            "/tmp/out.mkv",
            Array.Empty<string>());

        var vf = args.ToList();
        var index = vf.IndexOf("-vf");
        Assert.True(index >= 0, "expected a filter chain");
        Assert.Equal(1, vf.Count(a => string.Equals(a, "-vf", StringComparison.Ordinal)));
        Assert.Contains("hqdn3d", vf[index + 1], StringComparison.Ordinal);
        Assert.Contains("scale", vf[index + 1], StringComparison.Ordinal);
    }

    // On a copy profile they used to be dropped without a word. A filter still is — it cannot apply to a
    // stream that is not re-encoded — but everything else the admin configured is honoured.
    [Fact]
    public void ExtraVideoArgsSurviveACopyProfile_MinusTheFilter()
    {
        var args = FfmpegCommandBuilder.BuildArguments(
            new EncodingProfile
            {
                VideoCodec = "copy", AudioCodec = "copy", Container = "matroska",
                ExtraVideoArgs = "-vf hqdn3d -bsf:v h264_mp4toannexb"
            },
            new MediaProbeInfo { VideoCodec = "h264", Width = 1920, Height = 1080 },
            new List<ResolutionPreset>(),
            "/media/Movie.mkv",
            "/tmp/out.mkv",
            Array.Empty<string>());

        Assert.Contains("-bsf:v", args, StringComparer.Ordinal);
        Assert.DoesNotContain("-vf", args, StringComparer.Ordinal);
        Assert.DoesNotContain("hqdn3d", args, StringComparer.Ordinal);
    }

    // ---- audio / container negotiation ----
    //
    // The counterpart of the subtitle negotiation. A muxer handed an audio codec it has no tag for
    // rejects the output header and the whole job dies in seconds, so a track the container cannot store
    // is re-encoded rather than copied.

    [Fact]
    public void CopyAudio_Mp4_ReEncodesOnlyTheTrackTheContainerCannotStore()
    {
        var p = BaseProfile();
        p.AudioCodec = "copy";
        var s = Source();
        s.AudioStreams = new[]
        {
            new AudioStreamInfo { Codec = "truehd", Channels = 8 }, // mp4 has no tag for TrueHD
            new AudioStreamInfo { Codec = "ac3", Channels = 6 },    // mp4 stores ac-3 fine
        };
        var cmd = Build(p, s);

        Assert.Contains("-c:a:0 aac", cmd);
        Assert.Contains("-b:a:0 256k", cmd);
        Assert.Contains("-c:a:1 copy", cmd);
        Assert.DoesNotContain("-c:a copy", cmd);
    }

    // "Copy" is honoured as far as the container allows and no further: a track that must be re-encoded
    // is not also downmixed, because that is a second change the admin never asked for.
    [Fact]
    public void CopyAudio_ForcedReEncode_DoesNotAlsoDownmix()
    {
        var p = BaseProfile();
        p.AudioCodec = "copy";
        p.ChannelPolicy = AudioChannelPolicy.CapStereo;
        var s = Source();
        s.AudioStreams = new[] { new AudioStreamInfo { Codec = "truehd", Channels = 8 } };
        var cmd = Build(p, s);

        Assert.Contains("-c:a:0 aac", cmd);
        Assert.DoesNotContain("-ac:a:0", cmd);
    }

    [Fact]
    public void CopyAudio_Matroska_StillWholesaleCopy_ForEveryCodec()
    {
        var p = BaseProfile();
        p.AudioCodec = "copy";
        p.Container = "mkv";
        var s = Source();
        s.AudioStreams = new[]
        {
            new AudioStreamInfo { Codec = "truehd", Channels = 8 },
            new AudioStreamInfo { Codec = "dts", Channels = 6 },
        };
        var cmd = Build(p, s);

        Assert.Contains("-c:a copy", cmd);
        Assert.DoesNotContain("-c:a:0", cmd);
    }

    [Fact]
    public void CopyAudio_Webm_ReEncodesAacToLibopus()
    {
        var p = WebmProfile();
        p.AudioCodec = "copy";
        var s = Source();
        s.AudioStreams = new[] { new AudioStreamInfo { Codec = "aac", Channels = 2 } };
        var cmd = Build(p, s);

        Assert.Contains("-c:a:0 libopus", cmd);
        Assert.DoesNotContain("-c:a copy", cmd);
    }

    // The profile's own target can be the unstorable one. Copying the source's aac track into webm
    // because it "matches the profile" is exactly the failure this closes.
    [Fact]
    public void AacTarget_Webm_SubstitutesOpus_AndNeverCopiesTheAacTrack()
    {
        var p = WebmProfile();
        var s = Source();
        s.AudioStreams = new[]
        {
            new AudioStreamInfo { Codec = "aac", Channels = 2 },
            new AudioStreamInfo { Codec = "ac3", Channels = 6 },
        };
        var cmd = Build(p, s);

        Assert.Contains("-c:a:0 libopus", cmd);
        Assert.Contains("-c:a:1 libopus", cmd);
        Assert.DoesNotContain("copy", cmd);
        Assert.DoesNotContain(" aac", cmd);
    }

    [Fact]
    public void AacTarget_Webm_NoPerStreamInfo_StillSubstitutesOpus()
    {
        var cmd = Build(WebmProfile(), Source());

        Assert.Contains("-c:a libopus", cmd);
        Assert.DoesNotContain("-c:a aac", cmd);
    }

    // Regression guard: the ordinary mp4 case must emit exactly what it always did.
    [Fact]
    public void AacTarget_Mp4_AacTrackIsStillCopiedVerbatim()
    {
        var p = BaseProfile();
        var s = Source();
        s.AudioStreams = new[]
        {
            new AudioStreamInfo { Codec = "aac", Channels = 2 },
            new AudioStreamInfo { Codec = "truehd", Channels = 8 },
        };
        var cmd = Build(p, s);

        Assert.Contains("-c:a:0 copy", cmd);
        Assert.Contains("-c:a:1 aac", cmd);
    }

    [Fact]
    public void OpusTarget_Webm_OpusTrackIsCopied()
    {
        var p = WebmProfile();
        p.AudioCodec = "opus";
        p.AudioEncoder = "libopus";
        var s = Source();
        s.AudioStreams = new[] { new AudioStreamInfo { Codec = "opus", Channels = 2 } };

        Assert.Contains("-c:a:0 copy", Build(p, s));
    }

    // ---- hardware decoding ----

    private static List<string> BuildArgs(EncodingProfile p, MediaProbeInfo s, string? hwaccel)
    {
        return FfmpegCommandBuilder.BuildArguments(p, s, Presets, "/in.mkv", "/out.mp4", null, hwaccel).ToList();
    }

    [Fact]
    public void HardwareDecoder_IsEmittedBeforeTheInputItAppliesTo()
    {
        var args = BuildArgs(BaseProfile(), Source(), "cuda");
        var h = args.IndexOf("-hwaccel");
        var i = args.IndexOf("-i");

        Assert.True(h >= 0, "the configured hardware decoder must be emitted");
        Assert.Equal("cuda", args[h + 1]);
        Assert.True(h < i, "-hwaccel is an input option and must precede -i");
    }

    // Naming an output format would keep frames in GPU memory, where the scale and tonemap chains this
    // builder emits cannot read them. The frames are copied back instead, so the two compose.
    [Fact]
    public void HardwareDecoder_NeverPinsFramesToGpuMemory()
    {
        var p = BaseProfile();
        p.ResolutionMode = ResolutionMode.CapHeight;
        p.MaxHeight = 1080;
        var cmd = string.Join(" ", BuildArgs(p, Source(3840, 2160), "cuda"));

        Assert.Contains("-hwaccel cuda", cmd);
        Assert.DoesNotContain("-hwaccel_output_format", cmd);
        Assert.Contains("scale=", cmd);
    }

    // A copy profile decodes nothing, so initialising a hardware device could only fail.
    [Fact]
    public void HardwareDecoder_NotEmittedForACopyVideoProfile()
    {
        var p = BaseProfile();
        p.VideoCodec = "copy";

        Assert.DoesNotContain("-hwaccel", BuildArgs(p, Source(), "cuda"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void HardwareDecoder_UnsetMeansSoftwareDecoding(string? configured)
    {
        Assert.DoesNotContain("-hwaccel", BuildArgs(BaseProfile(), Source(), configured));
    }

    // The value is an admin-editable config string. It reaches ffmpeg through ArgumentList so it is never
    // shell-interpreted, but a value carrying a space must not smuggle a second option in either.
    [Theory]
    [InlineData("cuda -i /etc/passwd")]
    [InlineData("cuda;rm -rf /")]
    [InlineData("../../evil")]
    public void HardwareDecoder_MalformedValueIsDropped(string configured)
    {
        Assert.DoesNotContain("-hwaccel", BuildArgs(BaseProfile(), Source(), configured));
    }

    [Fact]
    public void HardwareDecoder_IsNormalisedToLowercase()
    {
        var args = BuildArgs(BaseProfile(), Source(), "VideoToolbox");

        Assert.Equal("videotoolbox", args[args.IndexOf("-hwaccel") + 1]);
    }
}
