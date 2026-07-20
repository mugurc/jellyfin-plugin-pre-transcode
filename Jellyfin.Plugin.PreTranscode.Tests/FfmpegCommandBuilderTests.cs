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
    public void Webm_ConvertsTextSubtitlesToWebvtt()
    {
        var p = BaseProfile();
        p.Container = "webm";
        var s = Source();
        s.SubtitleStreams = new[]
        {
            new SubtitleStreamInfo { Codec = "webvtt" }, // -> copy
            new SubtitleStreamInfo { Codec = "subrip" }, // webm holds only webvtt -> convert
        };
        var cmd = Build(p, s);
        Assert.Contains("-c:s:0 copy", cmd);
        Assert.Contains("-c:s:1 webvtt", cmd);
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
}
