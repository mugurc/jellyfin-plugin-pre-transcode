using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PreTranscode.Ffmpeg;
using Jellyfin.Plugin.PreTranscode.Media;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Jellyfin.Plugin.PreTranscode.Tests;

/// <summary>
/// Verifies the ffprobe result cache: an unchanged file is not re-probed, a changed one is. Skipped
/// automatically when no ffmpeg/ffprobe is present (e.g. on CI).
/// </summary>
[Trait("Category", "Integration")]
public class MediaProberCacheIntegrationTests
{
    private static string? Find(string name)
    {
        var candidates = new[]
        {
            @"C:\Program Files\Jellyfin\Server\" + name + ".exe",
            "/usr/lib/jellyfin-ffmpeg/" + name,
            "/usr/bin/" + name
        };
        foreach (var c in candidates)
        {
            if (File.Exists(c))
            {
                return c;
            }
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var exe = OperatingSystem.IsWindows() ? name + ".exe" : name;
        foreach (var dir in path.Split(Path.PathSeparator))
        {
            try
            {
                var full = Path.Combine(dir.Trim(), exe);
                if (File.Exists(full))
                {
                    return full;
                }
            }
            catch (ArgumentException)
            {
            }
        }

        return null;
    }

    [Fact]
    public async Task UnchangedFile_IsServedFromCache_ChangedFile_IsReprobed()
    {
        var ffmpeg = Find("ffmpeg");
        var ffprobe = Find("ffprobe");
        if (ffmpeg is null || ffprobe is null)
        {
            return; // no local ffmpeg — skip
        }

        var work = Path.Combine(Path.GetTempPath(), "pt-probecache-" + Path.GetRandomFileName());
        Directory.CreateDirectory(work);
        var source = Path.Combine(work, "clip.mkv");

        try
        {
            await ProcessRunner.RunAsync(
                ffmpeg,
                "-y -f lavfi -i testsrc=size=320x240:rate=24:duration=2 -c:v libx264 -preset ultrafast \"" + source + "\"",
                60000,
                CancellationToken.None);
            Assert.True(File.Exists(source), "failed to generate test clip");

            var encoder = new Mock<IMediaEncoder>();
            encoder.SetupGet(x => x.EncoderPath).Returns(ffmpeg);
            encoder.SetupGet(x => x.ProbePath).Returns(ffprobe);
            var prober = new MediaProber(encoder.Object, NullLogger<MediaProber>.Instance);

            var first = await prober.ProbeAsync(source, CancellationToken.None);
            var second = await prober.ProbeAsync(source, CancellationToken.None);
            Assert.NotNull(first);

            // A cache hit returns the very same instance; a re-probe would parse a fresh object.
            Assert.Same(first, second);

            // Change the file so its size/mtime differ, invalidating the cache entry.
            await ProcessRunner.RunAsync(
                ffmpeg,
                "-y -f lavfi -i testsrc=size=320x240:rate=24:duration=4 -c:v libx264 -preset ultrafast \"" + source + "\"",
                60000,
                CancellationToken.None);

            var third = await prober.ProbeAsync(source, CancellationToken.None);
            Assert.NotNull(third);
            Assert.NotSame(first, third);
        }
        finally
        {
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
