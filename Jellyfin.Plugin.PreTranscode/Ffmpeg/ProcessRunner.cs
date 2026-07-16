using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.PreTranscode.Ffmpeg;

/// <summary>
/// Minimal helper to run a child process (ffmpeg/ffprobe) and capture its combined output.
/// </summary>
internal static class ProcessRunner
{
    /// <summary>
    /// Runs a process to completion, capturing combined stdout+stderr.
    /// </summary>
    /// <param name="fileName">Executable path.</param>
    /// <param name="arguments">Command-line arguments.</param>
    /// <param name="timeoutMilliseconds">Maximum time to wait before the process is killed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The combined standard output and standard error text.</returns>
    public static async Task<string> RunAsync(string fileName, string arguments, int timeoutMilliseconds, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = new Process { StartInfo = startInfo };
        var output = new StringBuilder();
        var syncLock = new object();

        void OnData(object sender, DataReceivedEventArgs e)
        {
            if (e.Data is not null)
            {
                lock (syncLock)
                {
                    output.AppendLine(e.Data);
                }
            }
        }

        process.OutputDataReceived += OnData;
        process.ErrorDataReceived += OnData;

        if (!process.Start())
        {
            throw new InvalidOperationException(
                string.Format(CultureInfo.InvariantCulture, "Failed to start process '{0}'.", fileName));
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeoutMilliseconds);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new TimeoutException(string.Format(
                CultureInfo.InvariantCulture,
                "Process '{0} {1}' timed out after {2} ms.",
                fileName,
                arguments,
                timeoutMilliseconds));
        }

        lock (syncLock)
        {
            return output.ToString();
        }
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
            // Process already exited; nothing to do.
        }
    }
}
