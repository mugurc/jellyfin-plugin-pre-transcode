using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PreTranscode.Configuration;
using Jellyfin.Plugin.PreTranscode.Encoding;
using Jellyfin.Plugin.PreTranscode.Ffmpeg;
using Jellyfin.Plugin.PreTranscode.Media;
using Jellyfin.Plugin.PreTranscode.Rules;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Jellyfin.Plugin.PreTranscode.Tests;

/// <summary>
/// Real-ffmpeg checks for sources that encoded "successfully" into something clients cannot use, or did
/// not encode at all: surround audio in a non-standard layout, portrait phone video, odd frame sizes,
/// text subtitles in a container the builder does not model, and HEVC tagged for no Apple client.
/// <para>
/// Each one runs the real builder against the real binary and asserts on the file ffprobe reads back,
/// in terms that hold on every ffmpeg the plugin meets: CI's Ubuntu 6.1.1, jellyfin-ffmpeg 7.1.x and
/// current releases. Skipped automatically when no ffmpeg/ffprobe is present.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class ClientCompatibilityIntegrationTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "pretranscode-compat-" + Path.GetRandomFileName());

    public ClientCompatibilityIntegrationTests()
    {
        Directory.CreateDirectory(_work);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_work, recursive: true);
        }
        catch (IOException)
        {
            // best effort
        }
    }

    /// <summary>
    /// An AC-3 5.1 track decodes as 5.1(side). Re-encoded as it is, jellyfin-ffmpeg 7.1.4's aac writes a
    /// PCE that Apple clients reject — the film plays with no main audio — and ffprobe reads back an
    /// unknown layout; libopus (stock builds) refuses the layout and fails the job. Given the standard
    /// layout for six channels, both produce plain 5.1.
    /// </summary>
    /// <param name="audioCodec">The profile's audio codec.</param>
    /// <param name="audioEncoder">The profile's audio encoder.</param>
    /// <param name="container">The profile's container.</param>
    /// <returns>A task.</returns>
    [Theory]
    [InlineData("aac", "aac", "mp4")]
    [InlineData("opus", "libopus", "matroska")]
    public async Task SideSurround_IsReEncodedToStandard51(string audioCodec, string audioEncoder, string container)
    {
        if (!TryFindBinaries(out var ffmpeg, out var ffprobe))
        {
            return;
        }

        var source = Path.Combine(_work, "side51.mkv");
        await ProcessRunner.RunAsync(
            ffmpeg,
            new[]
            {
                "-y", "-f", "lavfi", "-i", "testsrc=size=320x180:rate=24:duration=2",
                "-f", "lavfi", "-i", "sine=frequency=440:duration=2,aformat=channel_layouts=5.1(side)",
                "-c:v", "libx264", "-preset", "ultrafast", "-c:a", "ac3", "-shortest", source
            },
            60000,
            CancellationToken.None);

        Assert.Equal("6,5.1(side)", await StreamEntries(ffprobe, source, "a:0", "stream=channels,channel_layout"));

        var profile = Profile(container);
        profile.VideoCodec = "copy";
        profile.AudioCodec = audioCodec;
        profile.AudioEncoder = audioEncoder;
        var (output, _, _) = await EncodeAndVerify(ffmpeg, ffprobe, profile, source);

        Assert.Equal("6,5.1", await StreamEntries(ffprobe, output, "a:0", "stream=channels,channel_layout"));
    }

    /// <summary>
    /// A portrait phone clip: the coded frame is landscape and the display matrix turns it. ffmpeg rotates
    /// while decoding, so a re-encode is upright — and the probe has to say so, or the longest-edge cap
    /// enlarges the narrow side and the verifier rejects the result as the wrong shape. A copy keeps the
    /// matrix instead, and must verify too.
    /// </summary>
    /// <param name="videoCodec">The profile's video codec.</param>
    /// <param name="container">The profile's container.</param>
    /// <returns>A task.</returns>
    [Theory]
    [InlineData("h264", "mp4")]
    [InlineData("copy", "mp4")]
    [InlineData("copy", "matroska")]
    public async Task RotatedSource_IsCappedOnItsDisplayedSizeAndVerified(string videoCodec, string container)
    {
        if (!TryFindBinaries(out var ffmpeg, out var ffprobe))
        {
            return;
        }

        var landscape = Path.Combine(_work, "landscape.mp4");
        var source = Path.Combine(_work, "portrait.mp4");
        await ProcessRunner.RunAsync(
            ffmpeg,
            new[]
            {
                "-y", "-f", "lavfi", "-i", "testsrc=size=640x360:rate=24:duration=2",
                "-f", "lavfi", "-i", "sine=frequency=440:duration=2",
                "-c:v", "libx264", "-preset", "ultrafast", "-c:a", "aac", "-shortest", landscape
            },
            60000,
            CancellationToken.None);
        await ProcessRunner.RunAsync(
            ffmpeg,
            new[] { "-y", "-display_rotation:v:0", "90", "-i", landscape, "-c", "copy", source },
            60000,
            CancellationToken.None);

        if (await StreamEntries(ffprobe, source, "v:0", "stream_side_data=rotation") is not ("90" or "-90"))
        {
            return; // This ffmpeg cannot author a display matrix; nothing to prove here.
        }

        var info = await Prober(ffmpeg, ffprobe).ProbeAsync(source, CancellationToken.None);
        Assert.NotNull(info);
        Assert.Equal(360, info!.Width);
        Assert.Equal(640, info.Height);
        Assert.False(info.HasContainerCrop);

        var profile = Profile(container);
        profile.VideoCodec = videoCodec;
        profile.ResolutionMode = ResolutionMode.CapLongestEdge;
        profile.MaxWidth = 320;
        var (_, outInfo, args) = await EncodeAndVerify(ffmpeg, ffprobe, profile, source);

        if (videoCodec != "copy")
        {
            // The long edge is the height, so that is what the cap fixes.
            Assert.Contains("scale=-2:320", args);
            Assert.Equal(180, outInfo.Width);
            Assert.Equal(320, outInfo.Height);
        }
    }

    /// <summary>
    /// libx264 and libx265 refuse an odd dimension in 4:2:0 ("height not divisible by 2"), and nothing
    /// scaled this source, so the job died on every attempt. The builder now drops the odd row; the
    /// verifier accepts that as the same picture, and the result is compliant so it is not queued again.
    /// </summary>
    /// <param name="videoCodec">The profile's video codec.</param>
    /// <param name="videoEncoder">The profile's video encoder.</param>
    /// <returns>A task.</returns>
    [Theory]
    [InlineData("h264", "libx264")]
    [InlineData("hevc", "libx265")]
    public async Task OddFrameSize_IsEncodedToTheNearestEvenSize(string videoCodec, string videoEncoder)
    {
        if (!TryFindBinaries(out var ffmpeg, out var ffprobe))
        {
            return;
        }

        var source = Path.Combine(_work, "odd.mkv");
        await ProcessRunner.RunAsync(
            ffmpeg,
            new[]
            {
                "-y", "-f", "lavfi", "-i", "testsrc=size=321x181:rate=24:duration=2",
                "-f", "lavfi", "-i", "sine=frequency=440:duration=2",
                "-c:v", "mpeg4", "-c:a", "aac", "-shortest", source
            },
            60000,
            CancellationToken.None);

        var profile = Profile("matroska");
        profile.VideoCodec = videoCodec;
        profile.VideoEncoder = videoEncoder;
        var (_, outInfo, _) = await EncodeAndVerify(ffmpeg, ffprobe, profile, source);

        Assert.Equal(320, outInfo.Width);
        Assert.Equal(180, outInfo.Height);
        Assert.False(ProfileComplianceChecker.NeedsWork(profile, outInfo, Array.Empty<ResolutionPreset>(), out var reason), reason);
    }

    /// <summary>
    /// mpegts holds no text subtitles in a form ffmpeg will pick by default, and the builder used to map a
    /// subrip track there anyway with no codec for it: "Error selecting an encoder", every attempt.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task TextSubtitle_IntoMpegts_DoesNotFailTheJob()
    {
        if (!TryFindBinaries(out var ffmpeg, out var ffprobe))
        {
            return;
        }

        var srt = Path.Combine(_work, "s.srt");
        await File.WriteAllTextAsync(srt, "1\n00:00:00,000 --> 00:00:01,500\nhello\n");
        var source = Path.Combine(_work, "withsrt.mkv");
        await ProcessRunner.RunAsync(
            ffmpeg,
            new[]
            {
                "-y", "-f", "lavfi", "-i", "testsrc=size=320x180:rate=24:duration=2",
                "-f", "lavfi", "-i", "sine=frequency=440:duration=2", "-i", srt,
                "-map", "0", "-map", "1", "-map", "2",
                "-c:v", "libx264", "-preset", "ultrafast", "-c:a", "aac", "-c:s", "srt", "-shortest", source
            },
            60000,
            CancellationToken.None);

        var info = await Prober(ffmpeg, ffprobe).ProbeAsync(source, CancellationToken.None);
        Assert.NotNull(info);
        Assert.Single(info!.SubtitleStreams);

        await EncodeAndVerify(ffmpeg, ffprobe, Profile("mpegts"), source);
    }

    /// <summary>
    /// ffmpeg tags HEVC in mp4 as hev1 unless told otherwise, and Apple clients will not direct-play
    /// that. Both a re-encode and a stream copy must come out as hvc1.
    /// </summary>
    /// <param name="videoCodec">The profile's video codec.</param>
    /// <returns>A task.</returns>
    [Theory]
    [InlineData("hevc")]
    [InlineData("copy")]
    public async Task HevcInMp4_IsTaggedHvc1(string videoCodec)
    {
        if (!TryFindBinaries(out var ffmpeg, out var ffprobe))
        {
            return;
        }

        var source = Path.Combine(_work, "hevc.mkv");
        await ProcessRunner.RunAsync(
            ffmpeg,
            new[]
            {
                "-y", "-f", "lavfi", "-i", "testsrc=size=320x180:rate=24:duration=2",
                "-f", "lavfi", "-i", "sine=frequency=440:duration=2",
                "-c:v", "libx265", "-preset", "ultrafast", "-c:a", "aac", "-shortest", source
            },
            60000,
            CancellationToken.None);

        var profile = Profile("mp4");
        profile.VideoCodec = videoCodec;
        profile.VideoEncoder = "libx265";
        var (output, _, _) = await EncodeAndVerify(ffmpeg, ffprobe, profile, source);

        Assert.Equal("hvc1", await StreamEntries(ffprobe, output, "v:0", "stream=codec_tag_string"));
    }

    /// <summary>
    /// H.264 High 10 and High 4:4:4 used to be skipped as already compliant, because only the codec name
    /// was compared. Both now need work, and what the builder makes of them is compliant — so the file is
    /// encoded once, not on every sweep.
    /// </summary>
    /// <param name="pixelFormat">The source pixel format.</param>
    /// <returns>A task.</returns>
    [Theory]
    [InlineData("yuv420p10le")]
    [InlineData("yuv444p")]
    public async Task HighProfileH264_NeedsWorkOnce(string pixelFormat)
    {
        if (!TryFindBinaries(out var ffmpeg, out var ffprobe))
        {
            return;
        }

        var source = Path.Combine(_work, "high.mkv");
        await ProcessRunner.RunAsync(
            ffmpeg,
            new[]
            {
                "-y", "-f", "lavfi", "-i", "testsrc=size=320x180:rate=24:duration=2",
                "-f", "lavfi", "-i", "sine=frequency=440:duration=2",
                "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", pixelFormat, "-c:a", "aac", "-shortest", source
            },
            60000,
            CancellationToken.None);

        var info = await Prober(ffmpeg, ffprobe).ProbeAsync(source, CancellationToken.None);
        if (info is null || !string.Equals(info.PixelFormat, pixelFormat, StringComparison.Ordinal))
        {
            return; // This build's libx264 cannot produce the format; nothing to prove here.
        }

        var profile = Profile("matroska");
        Assert.True(ProfileComplianceChecker.NeedsWork(profile, info, Array.Empty<ResolutionPreset>(), out _));

        var (_, outInfo, _) = await EncodeAndVerify(ffmpeg, ffprobe, profile, source);
        Assert.Equal("yuv420p", outInfo.PixelFormat);
        Assert.False(ProfileComplianceChecker.NeedsWork(profile, outInfo, Array.Empty<ResolutionPreset>(), out var reason), reason);
    }

    private static bool TryFindBinaries(out string ffmpeg, out string ffprobe)
    {
        ffmpeg = FfmpegTestBinaries.Find("ffmpeg") ?? string.Empty;
        ffprobe = FfmpegTestBinaries.Find("ffprobe") ?? string.Empty;
        return ffmpeg.Length > 0 && ffprobe.Length > 0; // No ffmpeg on this machine — nothing to exercise.
    }

    private static EncodingProfile Profile(string container)
    {
        return new EncodingProfile
        {
            VideoCodec = "h264", VideoEncoder = "libx264", VideoQualityMode = QualityMode.Crf, Crf = 30,
            Preset = "ultrafast", AudioCodec = "aac", AudioEncoder = "aac", AudioBitrateKbps = 128,
            Container = container, ResolutionMode = ResolutionMode.Unchanged, ChannelPolicy = AudioChannelPolicy.Unchanged
        };
    }

    private static MediaProber Prober(string ffmpeg, string ffprobe)
    {
        var encoder = new Mock<IMediaEncoder>();
        encoder.SetupGet(x => x.EncoderPath).Returns(ffmpeg);
        encoder.SetupGet(x => x.ProbePath).Returns(ffprobe);
        return new MediaProber(encoder.Object, NullLogger<MediaProber>.Instance);
    }

    // Probe, build, run and verify exactly as the executor does, failing the test with ffmpeg's own
    // words when any step does.
    private async Task<(string Output, MediaProbeInfo OutInfo, string Command)> EncodeAndVerify(
        string ffmpeg, string ffprobe, EncodingProfile profile, string source)
    {
        var prober = Prober(ffmpeg, ffprobe);
        var info = await prober.ProbeAsync(source, CancellationToken.None);
        Assert.NotNull(info);

        var output = Path.Combine(_work, "output" + OutputApplier.ContainerExtension(profile.Container));
        var args = FfmpegCommandBuilder.BuildArguments(profile, info!, Array.Empty<ResolutionPreset>(), source, output);
        var command = FfmpegCommandBuilder.ToCommandLine(args);

        var (exitCode, stdErr) = await FfmpegExecutor.RunAsync(ffmpeg, args, info!.DurationSeconds, null, CancellationToken.None);
        Assert.True(exitCode == 0, "ffmpeg failed: " + command + "\n" + stdErr);

        var (ok, reason) = await OutputVerifier.VerifyAsync(prober, output, info, CancellationToken.None);
        Assert.True(ok, "verification failed: " + reason + "\n" + command);

        var outInfo = await prober.ProbeAsync(output, CancellationToken.None);
        Assert.NotNull(outInfo);
        return (output, outInfo!, command);
    }

    // One stream's entries as ffprobe prints them in csv ("6,5.1"), for the facts MediaProbeInfo does not
    // carry because nothing but these tests needs them — and, for the display matrix, so the test knows
    // what it authored without asking the prober it is testing.
    private static async Task<string> StreamEntries(string ffprobe, string path, string stream, string entries)
    {
        var output = await ProcessRunner.RunAsync(
            ffprobe,
            new List<string> { "-v", "quiet", "-select_streams", stream, "-show_entries", entries, "-of", "csv=p=0", path },
            30000,
            CancellationToken.None);
        return output.Trim();
    }
}
