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
            CreateNoWindow = true
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
        var stderr = new StringBuilder();
        var stderrLock = new object();

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (stderrLock)
                {
                    stderr.AppendLine(e.Data);
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
            tail = Tail(stderr.ToString(), 60);
        }

        return (process.ExitCode, tail);
    }

    private static string Tail(string text, int lines)
    {
        var all = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        return all.Length <= lines ? text : string.Join("\n", all.Skip(all.Length - lines));
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(true);
            }
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
    }
}
