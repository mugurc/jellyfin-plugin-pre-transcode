using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PreTranscode.Ffmpeg;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Jellyfin.Plugin.PreTranscode.Tests;

[Trait("Category", "Integration")]
public class FfmpegCapabilitiesServiceTests
{
    // A probe that failed — the binary not where the path said, a timeout — used to be cached for the
    // life of the process, so an encoder looked as if it took no pixel formats at all and every 10-bit
    // source was encoded at 8 bits until the server was restarted.
    [Fact]
    public async Task GetEncoderPresets_AfterAFailedProbe_ProbesAgain()
    {
        var ffmpeg = FfmpegTestBinaries.Find("ffmpeg");
        if (ffmpeg is null)
        {
            return;
        }

        var path = Path.Combine(Path.GetTempPath(), "pt-missing-" + Path.GetRandomFileName(), "ffmpeg");
        var encoder = new Mock<IMediaEncoder>();
        encoder.SetupGet(e => e.EncoderPath).Returns(() => path);
        using var service = new FfmpegCapabilitiesService(encoder.Object, NullLogger<FfmpegCapabilitiesService>.Instance);

        var failed = await service.GetEncoderPresetsAsync("libx264", CancellationToken.None);
        Assert.Empty(failed.PixelFormats);

        path = ffmpeg;
        var probed = await service.GetEncoderPresetsAsync("libx264", CancellationToken.None);
        Assert.Contains("yuv420p", probed.PixelFormats);
    }
}
