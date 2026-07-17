using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Jellyfin.Plugin.PreTranscode.Ffmpeg;

/// <summary>
/// Suspends and resumes a running child process (an ffmpeg encode) at the OS level, so a "pause" freezes
/// the encode and frees CPU without losing progress — resuming continues exactly where it left off. A
/// suspended process still holds its memory and open file handles; only scheduling stops.
/// </summary>
internal static partial class ProcessSuspender
{
    /// <summary>
    /// Suspends the process. Best-effort: a process that has already exited (or whose handle is gone) is
    /// silently ignored.
    /// </summary>
    /// <param name="process">The process to suspend.</param>
    public static void Suspend(Process process) => Signal(process, suspend: true);

    /// <summary>
    /// Resumes a previously-suspended process. Best-effort.
    /// </summary>
    /// <param name="process">The process to resume.</param>
    public static void Resume(Process process) => Signal(process, suspend: false);

    private static void Signal(Process process, bool suspend)
    {
        try
        {
            if (process.HasExited)
            {
                return;
            }

            if (OperatingSystem.IsWindows())
            {
                var handle = process.Handle;
                _ = suspend ? NtSuspendProcess(handle) : NtResumeProcess(handle);
            }
            else
            {
                // Signal NAMES (STOP/CONT) are portable across Linux and macOS; the numeric values differ
                // between them, so `kill -STOP`/`-CONT` is safer than a hard-coded signal number.
                UnixKill(process.Id, suspend ? "STOP" : "CONT");
            }
        }
        catch (InvalidOperationException)
        {
            // The process exited or its object was disposed between the HasExited check and the signal
            // (ObjectDisposedException derives from InvalidOperationException). Nothing to suspend.
        }
    }

    private static void UnixKill(int pid, string signal)
    {
        var startInfo = new ProcessStartInfo("kill")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-" + signal);
        startInfo.ArgumentList.Add(pid.ToString(CultureInfo.InvariantCulture));

        using var killer = Process.Start(startInfo);
        killer?.WaitForExit(5000);
    }

    [SupportedOSPlatform("windows")]
    [LibraryImport("ntdll.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int NtSuspendProcess(IntPtr processHandle);

    [SupportedOSPlatform("windows")]
    [LibraryImport("ntdll.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int NtResumeProcess(IntPtr processHandle);
}
