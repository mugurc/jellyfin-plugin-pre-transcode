using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.PreTranscode.Encoding;

/// <summary>
/// Runs ffmpeg for a transcode, streaming progress and capturing an stderr excerpt for diagnostics.
/// </summary>
internal static class FfmpegExecutor
{
    // How much of ffmpeg's stderr is kept for the failure excerpt shown on the queue page.
    private const int StdErrTailLines = 60;

    public static async Task<(int ExitCode, string StdErrTail)> RunAsync(
        string ffmpegPath,
        IReadOnlyList<string> arguments,
        double totalDurationSeconds,
        Action<double>? onProgress,
        CancellationToken cancellationToken,
        Action<Process>? onProcessStarted = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,

            // ffmpeg emits UTF-8 on its progress/stderr pipes; decode as such so a non-ASCII path in the
            // captured error tail is not mangled by the host's OEM code page on Windows.
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };

        startInfo.ArgumentList.Add("-nostdin");
        startInfo.ArgumentList.Add("-nostats");
        startInfo.ArgumentList.Add("-progress");
        startInfo.ArgumentList.Add("pipe:1");
        foreach (var arg in arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = startInfo };

        // Only the last few lines are ever read, so only they are kept. Accumulating the whole of stderr
        // cost real memory on the sources that need diagnosing most: a file with broken timestamps makes
        // ffmpeg print "non monotonically increasing dts" per packet, which on a 2-hour encode is a couple
        // of hundred thousand lines — tens of megabytes of live char[], held for the length of the encode
        // and multiplied by the configured concurrency.
        var stderr = new Queue<string>(StdErrTailLines + 1);
        var stderrLock = new object();

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (stderrLock)
                {
                    stderr.Enqueue(e.Data);
                    if (stderr.Count > StdErrTailLines)
                    {
                        stderr.Dequeue();
                    }
                }
            }
        };

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null || onProgress is null || totalDurationSeconds <= 0)
            {
                return;
            }

            if (e.Data.StartsWith("out_time_us=", StringComparison.Ordinal))
            {
                var raw = e.Data["out_time_us=".Length..];
                if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var microseconds) && microseconds >= 0)
                {
                    var percent = Math.Clamp(microseconds / 1_000_000d / totalDurationSeconds * 100d, 0d, 100d);
                    onProgress(percent);
                }
            }
        };

        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start ffmpeg.");
        }

        // Hand the live process to the caller so it can suspend/resume it (pause) or otherwise track it.
        onProcessStarted?.Invoke(process);

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        string tail;
        lock (stderrLock)
        {
            tail = string.Join("\n", stderr);
        }

        return (process.ExitCode, tail);
    }

    // Process.Kill only posts the termination; it returns before the child is gone. The caller deletes the
    // temp output as soon as this returns, so without the wait that delete raced a process still holding
    // the handle — on Windows it fails outright and a part-finished, multi-gigabyte encode is left in the
    // plugin's temp directory for ever. Bounded, because a wedged process must not hold up a shutdown.
    private static void TryKill(Process process)
    {
        try
        {
            if (process.HasExited)
            {
                return;
            }

            process.Kill(true);
            process.WaitForExit(10000);
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
        catch (SystemException)
        {
            // Win32Exception (access denied on an exiting process) or an aborted wait: the encode is being
            // torn down either way, and this runs while an OperationCanceledException is in flight.
        }
    }
}
