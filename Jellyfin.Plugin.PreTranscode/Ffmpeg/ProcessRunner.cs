using System;
using System.Collections.Generic;
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
    /// Runs a process to completion, capturing combined stdout+stderr. Use this overload only with a
    /// fully-static argument string; any value that contains a caller- or file-supplied token (a path,
    /// an encoder name) must use the <see cref="IReadOnlyList{T}"/> overload so each argument is passed
    /// verbatim rather than parsed out of one command line.
    /// </summary>
    /// <param name="fileName">Executable path.</param>
    /// <param name="arguments">Command-line arguments.</param>
    /// <param name="timeoutMilliseconds">Maximum time to wait before the process is killed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The combined standard output and standard error text.</returns>
    public static Task<string> RunAsync(string fileName, string arguments, int timeoutMilliseconds, CancellationToken cancellationToken)
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

        return RunAsync(startInfo, arguments, timeoutMilliseconds, cancellationToken);
    }

    /// <summary>
    /// Runs a process to completion, passing each argument verbatim via <see cref="ProcessStartInfo.ArgumentList"/>
    /// so quotes, spaces and dashes in a value (e.g. a media file path) can never be reinterpreted as
    /// additional options. Prefer this whenever any argument is not a compile-time constant.
    /// </summary>
    /// <param name="fileName">Executable path.</param>
    /// <param name="arguments">The ordered argument list; each element is one argv entry.</param>
    /// <param name="timeoutMilliseconds">Maximum time to wait before the process is killed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The combined standard output and standard error text.</returns>
    public static Task<string> RunAsync(string fileName, IReadOnlyList<string> arguments, int timeoutMilliseconds, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return RunAsync(startInfo, string.Join(' ', arguments), timeoutMilliseconds, cancellationToken);
    }

    private static async Task<string> RunAsync(ProcessStartInfo startInfo, string displayArguments, int timeoutMilliseconds, CancellationToken cancellationToken)
    {
        var fileName = startInfo.FileName;
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
                displayArguments,
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
