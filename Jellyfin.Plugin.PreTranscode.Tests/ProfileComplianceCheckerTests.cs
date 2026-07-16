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
}
