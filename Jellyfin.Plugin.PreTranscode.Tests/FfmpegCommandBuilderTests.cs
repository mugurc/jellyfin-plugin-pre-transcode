using System.Collections.Generic;
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
    public void MatroskaContainer_MapsSubtitlesAndUsesMatroskaMuxer()
    {
        var p = BaseProfile();
        p.Container = "mkv";
        var cmd = Build(p, Source());
        Assert.Contains("-f matroska", cmd);
        Assert.Contains("0:s?", cmd);
        Assert.Contains("-c:s copy", cmd);
        Assert.DoesNotContain("-movflags", cmd);
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
