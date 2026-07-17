using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PreTranscode.Encoding;
using Jellyfin.Plugin.PreTranscode.Media;

namespace Jellyfin.Plugin.PreTranscode.Tests;

public class OutputVerifierTests
{
    private sealed class FakeProber : IMediaProber
    {
        private readonly double _duration;

        public FakeProber(double duration) => _duration = duration;

        public Task<MediaProbeInfo?> ProbeAsync(string path, CancellationToken cancellationToken)
            => Task.FromResult<MediaProbeInfo?>(new MediaProbeInfo { DurationSeconds = _duration });
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
            var (ok, reason) = await OutputVerifier.VerifyAsync(new FakeProber(7080), output, 7200, CancellationToken.None);
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
            var (ok, _) = await OutputVerifier.VerifyAsync(new FakeProber(7199), output, 7200, CancellationToken.None);
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
            var (ok, _) = await OutputVerifier.VerifyAsync(new FakeProber(7160), output, 7200, CancellationToken.None);
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
            var (ok, reason) = await OutputVerifier.VerifyAsync(new FakeProber(0), output, 0, CancellationToken.None);
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
            var (ok, _) = await OutputVerifier.VerifyAsync(new FakeProber(1200), output, 0, CancellationToken.None);
            Assert.True(ok);
        }
        finally
        {
            File.Delete(output);
        }
    }
}
