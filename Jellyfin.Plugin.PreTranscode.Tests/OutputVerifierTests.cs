using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PreTranscode.Encoding;
using Jellyfin.Plugin.PreTranscode.Media;

namespace Jellyfin.Plugin.PreTranscode.Tests;

public class OutputVerifierTests
{
    private sealed class FakeProber : IMediaProber
    {
        private readonly MediaProbeInfo _info;

        public FakeProber(double duration) => _info = new MediaProbeInfo { DurationSeconds = duration };

        public FakeProber(MediaProbeInfo info) => _info = info;

        public Task<MediaProbeInfo?> ProbeAsync(string path, CancellationToken cancellationToken)
            => Task.FromResult<MediaProbeInfo?>(_info);
    }

    private static MediaProbeInfo Video(int width, int height, double duration = 7200, int audioTracks = 1)
    {
        return new MediaProbeInfo
        {
            DurationSeconds = duration,
            VideoCodec = "h264",
            Width = width,
            Height = height,
            AudioStreams = Enumerable.Range(0, audioTracks)
                .Select(_ => new AudioStreamInfo { Codec = "aac", Channels = 2 })
                .ToList()
        };
    }

    private static string NewOutputFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "pretranscode-ov-" + Path.GetRandomFileName() + ".mkv");
        File.WriteAllText(path, "not empty");
        return path;
    }

    [Fact]
    public async Task TruncatedOutput_OnLongSource_IsRejected()
    {
        var output = NewOutputFile();
        try
        {
            // 2-hour source, output ends 2 minutes short. A percentage-only tolerance (2% = 144s) used to
            // let this pass and then the original would be deleted.
            var (ok, reason) = await OutputVerifier.VerifyAsync(new FakeProber(7080), output, new MediaProbeInfo { DurationSeconds = 7200 }, CancellationToken.None);
            Assert.False(ok);
            Assert.Contains("duration", reason);
        }
        finally
        {
            File.Delete(output);
        }
    }

    [Fact]
    public async Task MatchingDuration_IsAccepted()
    {
        var output = NewOutputFile();
        try
        {
            var (ok, _) = await OutputVerifier.VerifyAsync(new FakeProber(7199), output, new MediaProbeInfo { DurationSeconds = 7200 }, CancellationToken.None);
            Assert.True(ok);
        }
        finally
        {
            File.Delete(output);
        }
    }

    [Fact]
    public async Task ImpreciseContainerDuration_WithinCap_IsAccepted()
    {
        var output = NewOutputFile();
        try
        {
            // An MPEG-TS/.ts recording whose header over-reports by ~40s: the accurately-measured output
            // legitimately differs by more than a few seconds and must not be rejected as truncated.
            var (ok, _) = await OutputVerifier.VerifyAsync(new FakeProber(7160), output, new MediaProbeInfo { DurationSeconds = 7200 }, CancellationToken.None);
            Assert.True(ok);
        }
        finally
        {
            File.Delete(output);
        }
    }

    [Fact]
    public async Task UnknownSourceDuration_ButOutputHasNone_IsRejected()
    {
        var output = NewOutputFile();
        try
        {
            // Source duration unknown (0), and the output itself reports no duration — the signature of an
            // encode that produced only a header. Must not silently pass.
            var (ok, reason) = await OutputVerifier.VerifyAsync(new FakeProber(0), output, new MediaProbeInfo { DurationSeconds = 0 }, CancellationToken.None);
            Assert.False(ok);
            Assert.Contains("duration", reason);
        }
        finally
        {
            File.Delete(output);
        }
    }

    [Fact]
    public async Task UnknownSourceDuration_OutputHasDuration_IsAccepted()
    {
        var output = NewOutputFile();
        try
        {
            var (ok, _) = await OutputVerifier.VerifyAsync(new FakeProber(1200), output, new MediaProbeInfo { DurationSeconds = 0 }, CancellationToken.None);
            Assert.True(ok);
        }
        finally
        {
            File.Delete(output);
        }
    }

    // The cover-art failure this gate exists for: duration is satisfied by the audio track alone, so a
    // 600x900 poster thumbnail standing in for a 1920x1080 film passed verification — and under Replace
    // in place the original was then deleted for it.
    [Fact]
    public async Task OutputWithTheWrongShape_IsRejected()
    {
        var output = NewOutputFile();
        try
        {
            var (ok, reason) = await OutputVerifier.VerifyAsync(
                new FakeProber(Video(600, 900)), output, Video(1920, 1080), CancellationToken.None);

            Assert.False(ok);
            Assert.Contains("shape", reason, System.StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(output);
        }
    }

    [Fact]
    public async Task OutputWithNoVideoStream_IsRejected()
    {
        var output = NewOutputFile();
        try
        {
            var (ok, reason) = await OutputVerifier.VerifyAsync(
                new FakeProber(new MediaProbeInfo { DurationSeconds = 7200 }),
                output,
                Video(1920, 1080),
                CancellationToken.None);

            Assert.False(ok);
            Assert.Contains("video stream", reason, System.StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(output);
        }
    }

    // Every audio track is mapped, so ending up with fewer means one failed to encode and ffmpeg carried
    // on — the file plays, just without the track someone wanted.
    [Fact]
    public async Task OutputThatLostAnAudioTrack_IsRejected()
    {
        var output = NewOutputFile();
        try
        {
            var (ok, reason) = await OutputVerifier.VerifyAsync(
                new FakeProber(Video(1920, 1080, audioTracks: 1)),
                output,
                Video(1920, 1080, audioTracks: 3),
                CancellationToken.None);

            Assert.False(ok);
            Assert.Contains("audio track", reason, System.StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(output);
        }
    }

    // Downscaling is the normal case and must still pass, rounding to even dimensions included.
    [Fact]
    public async Task DownscaledOutput_IsAccepted()
    {
        var output = NewOutputFile();
        try
        {
            var (ok, reason) = await OutputVerifier.VerifyAsync(
                new FakeProber(Video(1920, 800)), output, Video(3840, 1600), CancellationToken.None);

            Assert.True(ok, reason);
        }
        finally
        {
            File.Delete(output);
        }
    }

    // A source the prober found no video in is not evidence of anything; it must not start failing
    // encodes that worked before.
    [Fact]
    public async Task SourceWithoutVideo_SkipsTheShapeCheck()
    {
        var output = NewOutputFile();
        try
        {
            var (ok, reason) = await OutputVerifier.VerifyAsync(
                new FakeProber(Video(600, 900)),
                output,
                new MediaProbeInfo { DurationSeconds = 7200 },
                CancellationToken.None);

            Assert.True(ok, reason);
        }
        finally
        {
            File.Delete(output);
        }
    }
}
