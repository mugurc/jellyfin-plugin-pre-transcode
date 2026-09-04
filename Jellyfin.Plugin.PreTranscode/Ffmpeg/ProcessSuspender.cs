using System;
using System.Diagnostics;
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
    /// Gets the number of SIGSTOP on the running platform.
    /// </summary>
    /// <remarks>
    /// Signal numbers are not portable, and these two are the worst case: the BSD lineage (macOS,
    /// FreeBSD, iOS) numbers SIGSTOP 17 and SIGCONT 19, while Linux numbers SIGSTOP 19 and SIGCONT 18.
    /// So <b>19 means "stop" on one and "continue" on the other</b> — getting this wrong would not fail,
    /// it would silently do the opposite. Every architecture .NET supports on Linux uses that numbering
    /// (the MIPS and SPARC variants that differ are not .NET targets), and Android is Linux for this
    /// purpose while <see cref="OperatingSystem.IsLinux"/> reports it separately.
    /// </remarks>
    private static int StopSignal => IsLinuxLike ? 19 : 17;

    /// <summary>
    /// Gets the number of SIGCONT on the running platform. See <see cref="StopSignal"/>.
    /// </summary>
    private static int ContinueSignal => IsLinuxLike ? 18 : 19;

    private static bool IsLinuxLike => OperatingSystem.IsLinux() || OperatingSystem.IsAndroid();

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
                SysKill(process.Id, suspend ? StopSignal : ContinueSignal);
            }
        }
        catch (InvalidOperationException)
        {
            // The process exited or its object was disposed between the HasExited check and the signal
            // (ObjectDisposedException derives from InvalidOperationException). Nothing to suspend.
        }
    }

    // The signal is sent with the kill(2) syscall rather than by running /bin/kill.
    //
    // Spawning it was what made Pause hang. Measured on macOS: the SIGSTOP reached ffmpeg (it went to
    // state T) but Suspend never returned, so the queue worker was stuck holding a suspended encode with
    // nothing able to cancel or resume it. The child process was the only blocking construct in the
    // function — starting one and waiting for it, from a parent that had just had another child change
    // state — and removing it removes the hang by construction rather than by tuning a timeout.
    //
    // It is also the same shape as the Windows path just below, which has always called the OS directly.
    // The search-path attribute is required by CA5392 and is inert here: it is a Windows loader concept,
    // and this import is only ever called on Unix, where "libc" resolves through the platform loader.
    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int SysKill(int pid, int signal);

    [SupportedOSPlatform("windows")]
    [LibraryImport("ntdll.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int NtSuspendProcess(IntPtr processHandle);

    [SupportedOSPlatform("windows")]
    [LibraryImport("ntdll.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int NtResumeProcess(IntPtr processHandle);
}
