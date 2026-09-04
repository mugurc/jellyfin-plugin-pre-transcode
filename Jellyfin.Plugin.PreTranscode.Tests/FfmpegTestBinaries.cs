using System;
using System.IO;

namespace Jellyfin.Plugin.PreTranscode.Tests;

/// <summary>
/// Locates an ffmpeg/ffprobe binary for the integration tests, which run against the real encoder when
/// one is present and skip themselves when it is not (CI has none).
/// <para>
/// Shared because the three integration suites each grew their own copy and they had already drifted:
/// two searched <c>PATH</c> after the well-known install locations and one did not, so the end-to-end
/// transcode test silently skipped itself on any machine whose ffmpeg came from Homebrew, a user-local
/// install, or anywhere else not on the hardcoded list — exactly the machines a developer runs it on.
/// </para>
/// </summary>
internal static class FfmpegTestBinaries
{
    /// <summary>
    /// Finds <paramref name="name"/> ("ffmpeg" or "ffprobe"), or returns <c>null</c> when the machine
    /// has none and the calling test should skip.
    /// </summary>
    /// <param name="name">The binary name, without extension.</param>
    /// <returns>The full path, or <c>null</c>.</returns>
    public static string? Find(string name)
    {
        var exe = OperatingSystem.IsWindows() ? name + ".exe" : name;

        // The well-known Jellyfin locations first: on a server that has both, the tests should exercise
        // the binary the plugin will actually use rather than whichever one happens to be on PATH.
        var candidates = new[]
        {
            Path.Combine(@"C:\Program Files\Jellyfin\Server", exe),
            "/usr/lib/jellyfin-ffmpeg/" + name,
            "/usr/bin/" + name
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
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
                // A malformed PATH entry (illegal characters) is not worth failing a test lookup over.
            }
        }

        return null;
    }
}
