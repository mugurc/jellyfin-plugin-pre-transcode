using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Jellyfin.Plugin.PreTranscode.Ffmpeg;

namespace Jellyfin.Plugin.PreTranscode.Tests;

/// <summary>
/// Covers <see cref="ProcessSuspender"/> without needing ffmpeg.
/// <para>
/// The suspend path used to send its signal by starting <c>/bin/kill</c> and waiting for it, and on
/// macOS that wait never returned: the signal was delivered (the child really did stop) but
/// <c>Suspend</c> never handed control back, so pausing the queue wedged the worker while holding a
/// suspended encode. The only test that touched this needed a real ffmpeg, and CI has none — so it never
/// ran anywhere it would have failed. This one needs nothing but a shell utility, which is the point.
/// </para>
/// </summary>
public class ProcessSuspenderTests
{
    /// <summary>
    /// Signalling must return promptly. The defect was unbounded, so the exact threshold matters less
    /// than that there is one; a couple of seconds is far above what two syscalls need and far below the
    /// forever the old implementation took.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task SuspendAndResume_ReturnPromptly()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Windows signals through ntdll, which is a separate path and not the one that hung.
        }

        var startInfo = new ProcessStartInfo("sleep")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("30");

        using var process = Process.Start(startInfo)!;
        try
        {
            var elapsed = await Task.Run(() =>
            {
                var stopwatch = Stopwatch.StartNew();
                ProcessSuspender.Suspend(process);
                ProcessSuspender.Resume(process);
                stopwatch.Stop();
                return stopwatch.ElapsedMilliseconds;
            }).WaitAsync(TimeSpan.FromSeconds(15));

            // WaitAsync above is what keeps a regression from hanging the whole suite forever, which is
            // how this bug behaved: the run never finished and never reported anything.
            Assert.True(elapsed < 2000, $"suspend + resume took {elapsed} ms; they must not block");
            Assert.False(process.HasExited, "signalling must not have killed the process");
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                }
            }
            catch (InvalidOperationException)
            {
                // Already gone.
            }
        }
    }

    /// <summary>
    /// A process that has already exited is signalled harmlessly rather than throwing — every caller
    /// treats suspend/resume as best-effort.
    /// </summary>
    [Fact]
    public void Signalling_AnExitedProcess_DoesNotThrow()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var startInfo = new ProcessStartInfo("sleep")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("0");

        using var process = Process.Start(startInfo)!;
        process.WaitForExit(10000);

        ProcessSuspender.Suspend(process);
        ProcessSuspender.Resume(process);
    }
}
