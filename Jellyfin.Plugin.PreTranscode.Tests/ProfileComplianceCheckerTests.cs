using System;
using System.Collections.Generic;
using Jellyfin.Plugin.PreTranscode.Configuration;
using Jellyfin.Plugin.PreTranscode.Media;
using Jellyfin.Plugin.PreTranscode.Rules;

namespace Jellyfin.Plugin.PreTranscode.Tests;

public class ProfileComplianceCheckerTests
{
    private static readonly IReadOnlyList<ResolutionPreset> Presets = new[]
    {
        new ResolutionPreset { Id = "1080p", Name = "1080p", Width = 1920, Height = 1080 }
    };

    private static EncodingProfile Profile()
    {
        return new EncodingProfile
        {
            VideoCodec = "h264", AudioCodec = "aac", Container = "mp4",
            ResolutionMode = ResolutionMode.Unchanged, ChannelPolicy = AudioChannelPolicy.Unchanged,
            TonemapHdr = false
        };
    }

    private static MediaProbeInfo Info(string vc = "h264", string ac = "aac", string container = "mp4",
        int w = 1280, int h = 720, int ch = 2, bool hdr = false)
    {
        return new MediaProbeInfo { VideoCodec = vc, AudioCodec = ac, Container = container, Width = w, Height = h, AudioChannels = ch, IsHdr = hdr };
    }

    [Fact]
    public void MatchingSource_IsCompliant()
    {
        Assert.True(ProfileComplianceChecker.IsAlreadyCompliant(Profile(), Info(), Presets));
    }

    [Fact]
    public void ShouldSkipAsCompliant_SkipsByDefault()
    {
        Assert.True(ProfileComplianceChecker.ShouldSkipAsCompliant(Profile(), Info(), Presets));
    }

    // GitHub issue #4. The rule set was five rules of the form "video is HEVC AND duration in range AND
    // file size over N" — a profile meant to re-encode oversized H.265 down to a smaller H.265. The rules
    // matched, but the compliance check compares no size or bitrate dimension, so it declared every
    // already-H.265 file compliant and silently vetoed all of them. The opt-out is what makes such a
    // profile possible at all.
    [Fact]
    public void OversizedSourceInTheTargetCodec_IsVetoedUnlessTheProfileOptsOut()
    {
        var profile = new EncodingProfile
        {
            Name = "Shrink oversized H.265",
            VideoCodec = "hevc", AudioCodec = "copy", Container = "matroska",
            ResolutionMode = ResolutionMode.Unchanged, ChannelPolicy = AudioChannelPolicy.Unchanged
        };
        var oversized = new MediaProbeInfo
        {
            VideoCodec = "hevc", Container = "matroska,webm",
            Width = 1920, Height = 1080,
            DurationSeconds = 120 * 60,
            FileSizeBytes = 4000L * 1024 * 1024
        };

        // The compliance check itself is unchanged: on the dimensions it compares, this file matches.
        Assert.True(ProfileComplianceChecker.IsAlreadyCompliant(profile, oversized, Presets));
        Assert.True(ProfileComplianceChecker.ShouldSkipAsCompliant(profile, oversized, Presets));

        profile.SkipIfAlreadyCompliant = false;
        Assert.False(ProfileComplianceChecker.ShouldSkipAsCompliant(profile, oversized, Presets));
    }

    // Turning the skip off must not make the check itself lie — the executor still reports the reason,
    // and a genuinely non-compliant file must stay non-compliant either way.
    [Fact]
    public void OptingOut_DoesNotAffectSourcesThatGenuinelyNeedWork()
    {
        var profile = Profile();
        profile.SkipIfAlreadyCompliant = false;

        Assert.False(ProfileComplianceChecker.ShouldSkipAsCompliant(profile, Info(vc: "hevc"), Presets));
        Assert.True(ProfileComplianceChecker.NeedsWork(profile, Info(vc: "hevc"), Presets, out var reason));
        Assert.Equal("video codec differs", reason);
    }

    [Fact]
    public void DifferentVideoCodec_NeedsWork()
    {
        Assert.False(ProfileComplianceChecker.IsAlreadyCompliant(Profile(), Info(vc: "hevc"), Presets));
    }

    [Fact]
    public void DifferentAudioCodec_NeedsWork()
    {
        Assert.False(ProfileComplianceChecker.IsAlreadyCompliant(Profile(), Info(ac: "ac3"), Presets));
    }

    [Fact]
    public void ContainerAlias_IsCompliant()
    {
        // ffprobe reports mp4 as "mov,mp4,m4a,3gp,3g2,mj2"
        Assert.True(ProfileComplianceChecker.IsAlreadyCompliant(Profile(), Info(container: "mov,mp4,m4a,3gp,3g2,mj2"), Presets));
    }

    [Fact]
    public void NoAudioSource_IsCompliant_OnAudioDimension()
    {
        // A source with no audio track cannot be given one by transcoding, so it must not be flagged
        // non-compliant on the audio codec forever — which re-transcoded a silent file on every sweep.
        Assert.True(ProfileComplianceChecker.IsAlreadyCompliant(Profile(), Info(ac: string.Empty), Presets));
    }

    [Fact]
    public void EmptyProfileContainer_TreatedAsMp4()
    {
        // The builder muxes an empty container as mp4; the checker must agree so the encoder's own mp4
        // output is not reported non-compliant forever.
        var p = Profile();
        p.Container = string.Empty;
        Assert.True(ProfileComplianceChecker.IsAlreadyCompliant(p, Info(container: "mov,mp4,m4a,3gp,3g2,mj2"), Presets));
    }

    [Fact]
    public void ResolutionOverCap_NeedsWork()
    {
        var p = Profile();
        p.ResolutionMode = ResolutionMode.CapHeight;
        p.MaxHeight = 1080;
        Assert.False(ProfileComplianceChecker.IsAlreadyCompliant(p, Info(w: 3840, h: 2160), Presets));
        Assert.True(ProfileComplianceChecker.IsAlreadyCompliant(p, Info(w: 1280, h: 720), Presets));
    }

    [Fact]
    public void HdrWithTonemap_NeedsWork()
    {
        var p = Profile();
        p.TonemapHdr = true;
        Assert.False(ProfileComplianceChecker.IsAlreadyCompliant(p, Info(hdr: true), Presets));
        Assert.True(ProfileComplianceChecker.IsAlreadyCompliant(p, Info(hdr: false), Presets));
    }

    [Fact]
    public void ChannelsOverCap_NeedsWork()
    {
        var p = Profile();
        p.ChannelPolicy = AudioChannelPolicy.CapStereo;
        Assert.False(ProfileComplianceChecker.IsAlreadyCompliant(p, Info(ch: 6), Presets));
        Assert.True(ProfileComplianceChecker.IsAlreadyCompliant(p, Info(ch: 2), Presets));
    }

    [Fact]
    public void NeedsWork_ReportsReason()
    {
        ProfileComplianceChecker.NeedsWork(Profile(), Info(vc: "hevc"), Presets, out var reason);
        Assert.Contains("codec", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CopyVideo_OverCapAndHdr_IsCompliant_NoInfiniteReencode()
    {
        // "Leave my video alone, just normalise audio/container": video is copied, so the builder emits
        // no scale/tonemap filter. The checker must therefore ignore resolution and HDR, or it would flag
        // the encoder's own output as non-compliant forever and re-transcode it on every sweep.
        var p = Profile();
        p.VideoCodec = "copy";
        p.ResolutionMode = ResolutionMode.CapHeight;
        p.MaxHeight = 1080;
        p.TonemapHdr = true;

        Assert.True(ProfileComplianceChecker.IsAlreadyCompliant(p, Info(vc: "hevc", w: 3840, h: 2160, hdr: true), Presets));
    }

    [Fact]
    public void NonFirstAudioTrackDiffers_NeedsWork()
    {
        // Track 0 already matches the target; a later track (e.g. a foreign-language TrueHD 7.1) does not.
        // Considering only track 0 would skip the file forever while that track keeps forcing live
        // transcoding — exactly what pre-transcoding is meant to eliminate.
        var p = Profile();
        p.ChannelPolicy = AudioChannelPolicy.CapStereo;
        var info = Info(ac: "aac", ch: 2);
        info.AudioStreams = new[]
        {
            new AudioStreamInfo { Codec = "aac", Channels = 2 },
            new AudioStreamInfo { Codec = "truehd", Channels = 8 }
        };

        Assert.False(ProfileComplianceChecker.IsAlreadyCompliant(p, info, Presets));
    }

    [Fact]
    public void AllAudioTracksCompliant_IsCompliant()
    {
        var p = Profile();
        p.ChannelPolicy = AudioChannelPolicy.CapStereo;
        var info = Info(ac: "aac", ch: 2);
        info.AudioStreams = new[]
        {
            new AudioStreamInfo { Codec = "aac", Channels = 2 },
            new AudioStreamInfo { Codec = "aac", Channels = 2 }
        };

        Assert.True(ProfileComplianceChecker.IsAlreadyCompliant(p, info, Presets));
    }

    // The builder substitutes a codec the container can hold when the profile asks for one it cannot
    // (aac into webm). Compliance has to compare the source against that SUBSTITUTED codec: judging it
    // against the configured "aac" would find the encoder's own opus output non-compliant on every
    // sweep, re-encode it, and queue it again forever.
    [Fact]
    public void AacProfileInWebm_OpusSource_IsCompliant_NotReEncodedForever()
    {
        var p = Profile();
        p.VideoCodec = "vp9";
        p.AudioCodec = "aac";
        p.Container = "webm";

        var info = Info(vc: "vp9", ac: "opus", container: "matroska,webm");
        info.AudioStreams = new[] { new AudioStreamInfo { Codec = "opus", Channels = 2 } };

        Assert.True(ProfileComplianceChecker.IsAlreadyCompliant(p, info, Presets));
    }

    [Fact]
    public void AacProfileInWebm_AacSource_StillNeedsWork()
    {
        var p = Profile();
        p.VideoCodec = "vp9";
        p.AudioCodec = "aac";
        p.Container = "webm";

        var info = Info(vc: "vp9", ac: "aac", container: "matroska,webm");
        info.AudioStreams = new[] { new AudioStreamInfo { Codec = "aac", Channels = 2 } };

        Assert.True(ProfileComplianceChecker.NeedsWork(p, info, Presets, out var reason));
        Assert.Equal("audio codec differs", reason);
    }

    // The mp4 case is unchanged: no substitution happens, so the configured codec is still the one compared.
    [Fact]
    public void AacProfileInMp4_AacSource_IsStillCompliant()
    {
        var info = Info();
        info.AudioStreams = new[] { new AudioStreamInfo { Codec = "aac", Channels = 2 } };

        Assert.True(ProfileComplianceChecker.IsAlreadyCompliant(Profile(), info, Presets));
    }
    // Issue #14. A UHD remux whose container crops 280px off the top and bottom displays as 3840x1600, and
    // that is the picture the encode produces. Judging "already under a 1800-line cap" by the coded 2160
    // queues a file that needed nothing doing to it.
    [Fact]
    public void ContainerCroppedSource_UnderTheCap_IsCompliant()
    {
        var profile = Profile();
        profile.ResolutionMode = ResolutionMode.CapHeight;
        profile.MaxHeight = 1800;

        var info = Info(vc: "h264", w: 3840, h: 1600);
        info.CodedWidth = 3840;
        info.CodedHeight = 2160;

        Assert.True(ProfileComplianceChecker.IsAlreadyCompliant(profile, info, Presets));
    }

    // ---- pixel format ----
    //
    // The builder re-encodes H.264 High 10 and High 4:4:4 to 8-bit 4:2:0 (most clients cannot decode
    // either), but compliance compared only the codec name — so both were skipped as "already compliant"
    // and never reached the encoder that would have fixed them.

    private static MediaProbeInfo Pixels(string vc, string pixelFormat, int bitDepth, bool hdr = false, string container = "mp4")
    {
        var info = Info(vc: vc, container: container, hdr: hdr);
        info.PixelFormat = pixelFormat;
        info.BitDepth = bitDepth;
        return info;
    }

    [Fact]
    public void H264High10_UnderAnH264Profile_NeedsWork()
    {
        var p = Profile();
        p.Container = "matroska";
        var hi10p = Pixels("h264", "yuv420p10le", 10, container: "matroska,webm");

        Assert.True(ProfileComplianceChecker.NeedsWork(p, hi10p, Presets, out var reason));
        Assert.Equal("bit depth differs", reason);
    }

    [Fact]
    public void H264High444_UnderTheDefaultProfile_NeedsWork()
    {
        var hi444 = Pixels("h264", "yuv444p", 8, container: "mov,mp4,m4a,3gp,3g2,mj2");

        Assert.True(ProfileComplianceChecker.NeedsWork(Profile(), hi444, Presets, out var reason));
        Assert.Equal("chroma subsampling differs", reason);
    }

    // What the builder produces from either of them is then compliant, so it is not re-encoded forever.
    [Fact]
    public void TheEncodersOwnOutput_IsCompliant()
    {
        Assert.True(ProfileComplianceChecker.IsAlreadyCompliant(Profile(), Pixels("h264", "yuv420p", 8), Presets));
    }

    // Full-range 4:2:0 (phones, MJPEG-derived files) is still 8-bit 4:2:0.
    [Fact]
    public void FullRangeYuvj420p_IsCompliant()
    {
        Assert.True(ProfileComplianceChecker.IsAlreadyCompliant(Profile(), Pixels("h264", "yuvj420p", 8), Presets));
    }

    // Auto keeps 10 bits for HEVC when the encoder can, and compliance assumes it can. Assuming the
    // opposite would call every 10-bit HEVC file non-compliant, re-encode it to 10-bit HEVC with libx265,
    // and find the result non-compliant again on the next sweep.
    [Fact]
    public void TenBitHevc_UnderAnHevcAutoProfile_IsCompliant()
    {
        var p = Profile();
        p.VideoCodec = "hevc";
        p.Container = "matroska";

        Assert.True(ProfileComplianceChecker.IsAlreadyCompliant(p, Pixels("hevc", "yuv420p10le", 10, container: "matroska,webm"), Presets));
    }

    // ...and an untonemapped HDR source keeps its depth even on H.264.
    [Fact]
    public void TenBitHdrH264_NotTonemapped_IsCompliant()
    {
        Assert.True(ProfileComplianceChecker.IsAlreadyCompliant(Profile(), Pixels("h264", "yuv420p10le", 10, hdr: true), Presets));
    }

    [Fact]
    public void ForceYuv420p_TenBitHevc_NeedsWork()
    {
        var p = Profile();
        p.VideoCodec = "hevc";
        p.PixelFormatMode = PixelFormatMode.ForceYuv420p;

        Assert.True(ProfileComplianceChecker.NeedsWork(p, Pixels("hevc", "yuv420p10le", 10), Presets, out var reason));
        Assert.Equal("bit depth differs", reason);
    }

    // A probe that could not name the format is not evidence of a difference.
    [Fact]
    public void UnknownPixelFormat_IsNotWork()
    {
        Assert.True(ProfileComplianceChecker.IsAlreadyCompliant(Profile(), Pixels("h264", string.Empty, 0), Presets));
    }

    [Fact]
    public void CopyVideo_IgnoresThePixelFormat()
    {
        var p = Profile();
        p.VideoCodec = "copy";
        Assert.True(ProfileComplianceChecker.IsAlreadyCompliant(p, Pixels("h264", "yuv444p10le", 10), Presets));
    }

    // The two decisions are one decision. For every combination, compliance reports pixel-format work
    // exactly when the format the builder picks — given an encoder that offers 10-bit — differs from the
    // source in depth or chroma.
    [Theory]
    [InlineData("h264", PixelFormatMode.Auto)]
    [InlineData("hevc", PixelFormatMode.Auto)]
    [InlineData("av1", PixelFormatMode.Auto)]
    [InlineData("h264", PixelFormatMode.KeepSourceBitDepth)]
    [InlineData("hevc", PixelFormatMode.ForceYuv420p)]
    public void PixelFormatVerdict_AgreesWithTheBuilder(string codec, PixelFormatMode mode)
    {
        var capable = new[] { "yuv420p", "yuv420p10le", "yuv444p", "yuv444p10le" };
        var sources = new[]
        {
            ("yuv420p", 8), ("yuvj420p", 8), ("yuv420p10le", 10), ("yuv420p12le", 12),
            ("yuv422p", 8), ("yuv422p10le", 10), ("yuv444p", 8), ("yuv444p10le", 10), ("gbrp", 8),
        };

        foreach (var hdr in new[] { false, true })
        {
            foreach (var (pixelFormat, depth) in sources)
            {
                var p = Profile();
                p.VideoCodec = codec;
                p.PixelFormatMode = mode;
                var info = Pixels(codec, pixelFormat, depth, hdr: hdr);

                var chosen = Jellyfin.Plugin.PreTranscode.Encoding.FfmpegCommandBuilder.ChoosePixelFormat(p, info, capable)!;
                var builderChanges = MediaProber.BitDepthFromPixelFormat(chosen) != depth
                    || MediaProber.ChromaSubsampling(chosen) != MediaProber.ChromaSubsampling(pixelFormat);

                Assert.True(
                    builderChanges == ProfileComplianceChecker.NeedsWork(p, info, Presets, out var reason),
                    $"{codec}/{mode} {pixelFormat} hdr={hdr}: builder picks {chosen}, compliance says '{reason}'");
            }
        }
    }

}
