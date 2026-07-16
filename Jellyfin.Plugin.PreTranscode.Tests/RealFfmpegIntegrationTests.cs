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
    private static string? Find(string name)
    {
        var candidates = new[]
        {
            Path.Combine(@"C:\Program Files\Jellyfin\Server", name + ".exe"),
            "/usr/lib/jellyfin-ffmpeg/" + name,
            "/usr/bin/" + name
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    [Fact]
    public async Task EndToEnd_ProbeBuildRunVerify()
    {
        var ffmpeg = Find("ffmpeg");
        var ffprobe = Find("ffprobe");
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
            var (ok, reason) = await OutputVerifier.VerifyAsync(prober, output, info.DurationSeconds, CancellationToken.None);
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
}
