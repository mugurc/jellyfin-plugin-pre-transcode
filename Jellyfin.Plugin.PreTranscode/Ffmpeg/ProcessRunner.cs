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
    private static readonly AsyncLocal<SignalWatch?> CurrentWatch = new();

    /// <summary>
    /// Starts noting, for the calling async flow and until the returned watch is disposed, whether a
    /// child this runner starts is killed by a signal.
    /// <para>
    /// Callers that reach ffprobe through <see cref="Media.IMediaProber"/> only ever see "not media" when
    /// it fails, which is right for deciding about a file and wrong for deciding about a job: a probe
    /// killed by the SIGTERM that systemd sends to every process of a stopping service looks exactly like
    /// a broken output. Ambient rather than a parameter so that the prober in between needs no knowledge
    /// of it; an <see cref="AsyncLocal{T}"/> keeps one job's watch from seeing another job's probes.
    /// </para>
    /// </summary>
    /// <returns>The watch; dispose it to stop watching.</returns>
    public static SignalWatch WatchSignals()
    {
        var watch = new SignalWatch();
        CurrentWatch.Value = watch;
        return watch;
    }

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

            // ffprobe/ffmpeg emit UTF-8; decode as such rather than the host's OEM code page (which
            // mangles non-ASCII metadata on Windows).
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
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
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
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
        catch (OperationCanceledException)
        {
            // The caller's own token was cancelled (plugin shutdown / task abort). Kill the child too so
            // ffprobe/ffmpeg is not left running with no one enforcing its timeout; repeated cancellations
            // would otherwise accumulate orphaned processes.
            TryKill(process);
            throw;
        }

        if (CurrentWatch.Value is { } watch && TerminatingSignal(process.ExitCode) is { } signal)
        {
            watch.Record(signal);
        }

        lock (syncLock)
        {
            return output.ToString();
        }
    }

    /// <summary>
    /// The signal that killed a child, from its exit code: .NET reports a child terminated by signal N as
    /// exit code 128 + N on Unix. Only meaningful for a program whose own exit codes stay below 129, which
    /// ffprobe's do (it exits 0 or 1); Windows has no signals.
    /// </summary>
    /// <param name="exitCode">The child's exit code.</param>
    /// <returns>The signal number, or <c>null</c> when the child exited of its own accord.</returns>
    internal static int? TerminatingSignal(int exitCode)
    {
        return !OperatingSystem.IsWindows() && exitCode > 128 && exitCode <= 128 + 64 ? exitCode - 128 : null;
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

    // Only the flow that started the watch may end it; anything else leaves the current one in place.
    private static void EndWatch(SignalWatch watch)
    {
        if (ReferenceEquals(CurrentWatch.Value, watch))
        {
            CurrentWatch.Value = null;
        }
    }

    /// <summary>
    /// Records the signal that killed a child started while it was the current watch. See
    /// <see cref="WatchSignals"/>.
    /// </summary>
    internal sealed class SignalWatch : IDisposable
    {
        private int _signal;

        /// <summary>
        /// Gets the signal that killed the most recent such child, or <c>null</c> if none was.
        /// </summary>
        public int? Signal
        {
            get
            {
                var signal = Volatile.Read(ref _signal);
                return signal == 0 ? null : signal;
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            EndWatch(this);
        }

        internal void Record(int signal)
        {
            Volatile.Write(ref _signal, signal);
        }
    }
}
