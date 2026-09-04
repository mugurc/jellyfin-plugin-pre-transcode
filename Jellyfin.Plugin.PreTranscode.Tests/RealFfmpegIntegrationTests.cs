using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PreTranscode.Configuration;
using Jellyfin.Plugin.PreTranscode.Encoding;
using Jellyfin.Plugin.PreTranscode.Ffmpeg;
using Jellyfin.Plugin.PreTranscode.Media;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Jellyfin.Plugin.PreTranscode.Tests;

/// <summary>
/// End-to-end validation of the real ffmpeg pipeline (probe -> build command -> run -> verify) using
/// the bundled Jellyfin ffmpeg. Skipped automatically when no ffmpeg/ffprobe is present (e.g. on CI).
/// </summary>
[Trait("Category", "Integration")]
public class RealFfmpegIntegrationTests
{
    [Fact]
    public async Task EndToEnd_ProbeBuildRunVerify()
    {
        var ffmpeg = FfmpegTestBinaries.Find("ffmpeg");
        var ffprobe = FfmpegTestBinaries.Find("ffprobe");
        if (ffmpeg is null || ffprobe is null)
        {
            return; // No local ffmpeg (CI) — nothing to exercise.
        }

        var work = Path.Combine(Path.GetTempPath(), "pretranscode-it-" + Path.GetRandomFileName());
        Directory.CreateDirectory(work);
        var source = Path.Combine(work, "source.mkv");
        var output = Path.Combine(work, "output.mp4");

        try
        {
            // 1. Generate a 3s HEVC + AAC clip.
            var genArgs = "-y -f lavfi -i testsrc=size=640x360:rate=24:duration=3 "
                + "-f lavfi -i sine=frequency=1000:duration=3 "
                + "-c:v libx265 -pix_fmt yuv420p -c:a aac -shortest \"" + source + "\"";
            await ProcessRunner.RunAsync(ffmpeg, genArgs, 60000, CancellationToken.None);
            Assert.True(File.Exists(source), "failed to generate test clip");

            var encoder = new Mock<IMediaEncoder>();
            encoder.SetupGet(x => x.EncoderPath).Returns(ffmpeg);
            encoder.SetupGet(x => x.ProbePath).Returns(ffprobe);
            var prober = new MediaProber(encoder.Object, NullLogger<MediaProber>.Instance);

            // 2. Probe the source.
            var info = await prober.ProbeAsync(source, CancellationToken.None);
            Assert.NotNull(info);
            Assert.Equal(640, info!.Width);
            Assert.Equal(360, info.Height);
            Assert.Equal("hevc", info.VideoCodec);
            Assert.True(info.DurationSeconds > 2 && info.DurationSeconds < 4);

            // 3. Build a transcode command to H.264 / AAC / MP4.
            var profile = new EncodingProfile
            {
                VideoCodec = "h264", VideoEncoder = "libx264", VideoQualityMode = QualityMode.Crf, Crf = 30,
                Preset = "ultrafast", AudioCodec = "aac", AudioEncoder = "aac", AudioBitrateKbps = 128,
                Container = "mp4", ResolutionMode = ResolutionMode.Unchanged, ChannelPolicy = AudioChannelPolicy.Unchanged
            };
            var args = FfmpegCommandBuilder.BuildArguments(profile, info, System.Array.Empty<ResolutionPreset>(), source, output);

            // 4. Run the real transcode.
            var (exitCode, stdErr) = await FfmpegExecutor.RunAsync(ffmpeg, args, info.DurationSeconds, null, CancellationToken.None);
            Assert.True(exitCode == 0, "ffmpeg failed: " + stdErr);

            // 5. Verify the output passes our verifier and is actually H.264.
            var (ok, reason) = await OutputVerifier.VerifyAsync(prober, output, info, CancellationToken.None);
            Assert.True(ok, "verification failed: " + reason);

            var outInfo = await prober.ProbeAsync(output, CancellationToken.None);
            Assert.NotNull(outInfo);
            Assert.Equal("h264", outInfo!.VideoCodec);
        }
        finally
        {
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch (IOException)
            {
                // best effort
            }
        }
    }

    /// <summary>
    /// The audio/container negotiation, against the real muxer that made it necessary. An mkv source's
    /// TrueHD track has no mp4 sample-entry tag, so the naive "copy audio into mp4" this plugin used to
    /// emit is rejected by the muxer and takes the whole encode down with it. Both halves are asserted:
    /// that the old command really does fail, and that the one the builder produces now succeeds and
    /// lands the track in a codec the container can hold.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task AudioNegotiation_TrueHdIntoMp4_ReEncodesWhereCopyWouldFail()
    {
        var ffmpeg = FfmpegTestBinaries.Find("ffmpeg");
        var ffprobe = FfmpegTestBinaries.Find("ffprobe");
        if (ffmpeg is null || ffprobe is null)
        {
            return; // No local ffmpeg (CI) — nothing to exercise.
        }

        var work = Path.Combine(Path.GetTempPath(), "pretranscode-audio-" + Path.GetRandomFileName());
        Directory.CreateDirectory(work);
        var source = Path.Combine(work, "source.mkv");

        try
        {
            // TrueHD's encoder is marked experimental, hence -strict -2. This is test scaffolding, not
            // something the plugin ever does.
            await ProcessRunner.RunAsync(
                ffmpeg,
                new[]
                {
                    "-y", "-f", "lavfi", "-i", "testsrc=size=320x180:rate=24:duration=2",
                    "-f", "lavfi", "-i", "sine=frequency=440:duration=2",
                    "-c:v", "libx264", "-preset", "ultrafast", "-crf", "30",
                    "-c:a", "truehd", "-strict", "-2", "-shortest", source
                },
                60000,
                CancellationToken.None);

            var encoder = new Mock<IMediaEncoder>();
            encoder.SetupGet(x => x.EncoderPath).Returns(ffmpeg);
            encoder.SetupGet(x => x.ProbePath).Returns(ffprobe);
            var prober = new MediaProber(encoder.Object, NullLogger<MediaProber>.Instance);

            var info = await prober.ProbeAsync(source, CancellationToken.None);
            if (info is null || info.AudioStreams.Count == 0
                || !string.Equals(info.AudioStreams[0].Codec, "truehd", StringComparison.OrdinalIgnoreCase))
            {
                return; // This build cannot produce TrueHD; nothing to prove here.
            }

            // The command the plugin used to build: audio copied wholesale into mp4.
            var naive = Path.Combine(work, "naive.mp4");
            var (naiveExit, _) = await FfmpegExecutor.RunAsync(
                ffmpeg,
                new[]
                {
                    "-y", "-hide_banner", "-i", source, "-map", "0:V:0", "-map", "0:a?",
                    "-c:v", "libx264", "-preset", "ultrafast", "-crf", "30",
                    "-c:a", "copy", "-f", "mp4", naive
                },
                info.DurationSeconds,
                null,
                CancellationToken.None);
            Assert.True(naiveExit != 0, "copying TrueHD into mp4 was expected to fail, so the negotiation has something to fix");

            // The command it builds now, from a profile that asks for exactly that.
            var output = Path.Combine(work, "output.mp4");
            var profile = new EncodingProfile
            {
                VideoCodec = "h264", VideoEncoder = "libx264", VideoQualityMode = QualityMode.Crf, Crf = 30,
                Preset = "ultrafast", AudioCodec = "copy", AudioEncoder = "aac", AudioBitrateKbps = 128,
                Container = "mp4", ResolutionMode = ResolutionMode.Unchanged, ChannelPolicy = AudioChannelPolicy.Unchanged
            };
            var args = FfmpegCommandBuilder.BuildArguments(profile, info, System.Array.Empty<ResolutionPreset>(), source, output);

            var (exitCode, stdErr) = await FfmpegExecutor.RunAsync(ffmpeg, args, info.DurationSeconds, null, CancellationToken.None);
            Assert.True(exitCode == 0, "ffmpeg failed: " + stdErr);

            var outInfo = await prober.ProbeAsync(output, CancellationToken.None);
            Assert.NotNull(outInfo);
            Assert.Single(outInfo!.AudioStreams);
            Assert.Equal("aac", outInfo.AudioStreams[0].Codec);
        }
        finally
        {
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch (IOException)
            {
                // best effort
            }
        }
    }

    /// <summary>
    /// Locks the <c>-hwaccels</c> parser to whatever the machine's real ffmpeg prints, rather than only
    /// to a captured sample that can go stale.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task HardwareAccelerators_ParseAgreesWithTheRealBinary()
    {
        var ffmpeg = FfmpegTestBinaries.Find("ffmpeg");
        if (ffmpeg is null)
        {
            return;
        }

        var raw = await ProcessRunner.RunAsync(ffmpeg, "-hide_banner -hwaccels", 30000, CancellationToken.None);
        var methods = FfmpegOutputParser.ParseHardwareAccelerators(raw);

        foreach (var method in methods)
        {
            // Never the header or any prose around it, and always something that survives the builder's
            // own validation — a name it rejects would be offered in the UI and then silently dropped.
            Assert.DoesNotContain(' ', method);
            Assert.Equal(method.ToLowerInvariant(), method);
            Assert.Contains(method, raw, StringComparison.Ordinal);
        }

        Assert.Distinct(methods);
    }
}
