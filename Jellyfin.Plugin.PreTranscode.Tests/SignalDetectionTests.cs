using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PreTranscode.Encoding;
using Jellyfin.Plugin.PreTranscode.Ffmpeg;

namespace Jellyfin.Plugin.PreTranscode.Tests;

/// <summary>
/// How the executor tells an encode that was stopped by a signal from one that failed: ffmpeg says so
/// in its log, and a signalled ffprobe shows up in its exit code.
/// </summary>
public class SignalDetectionTests
{
    [Theory]
    [InlineData("[libx264 @ 0x1] kb/s:3126.39\nExiting normally, received signal 15.", 15)]
    [InlineData("Exiting normally, received signal 2.", 2)]
    [InlineData("[out#0/matroska] Conversion failed!", null)]
    [InlineData("", null)]
    public void ReceivedSignal_ReadsWhatFfmpegReports(string stdErrTail, int? expected)
    {
        Assert.Equal(expected, FfmpegExecutor.ReceivedSignal(stdErrTail));
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(1, null)]
    [InlineData(143, 15)]
    [InlineData(130, 2)]
    [InlineData(137, 9)]
    public void TerminatingSignal_MapsUnixSignalExitCodes(int exitCode, int? expected)
    {
        Assert.Equal(OperatingSystem.IsWindows() ? null : expected, ProcessRunner.TerminatingSignal(exitCode));
    }

    [Fact]
    public async Task WatchSignals_SeesAChildKilledBySigterm_OnlyWhileWatching()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var killsItself = new[] { "-c", "kill -TERM $$" };

        await ProcessRunner.RunAsync("/bin/sh", killsItself, 10000, CancellationToken.None);

        using (var watch = ProcessRunner.WatchSignals())
        {
            await ProcessRunner.RunAsync("/bin/sh", new[] { "-c", "exit 1" }, 10000, CancellationToken.None);
            Assert.Null(watch.Signal);

            await ProcessRunner.RunAsync("/bin/sh", killsItself, 10000, CancellationToken.None);
            Assert.Equal(15, watch.Signal);
        }

        using var later = ProcessRunner.WatchSignals();
        Assert.Null(later.Signal);
    }
}
