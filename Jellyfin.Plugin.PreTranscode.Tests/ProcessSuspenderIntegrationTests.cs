using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Jellyfin.Plugin.PreTranscode.Ffmpeg;

namespace Jellyfin.Plugin.PreTranscode.Tests;

/// <summary>
/// Verifies OS-level suspend/resume against a real ffmpeg encode: a suspended process stops writing, a
/// resumed one continues. Skipped automatically when no ffmpeg is present (e.g. on CI).
/// </summary>
[Trait("Category", "Integration")]
public class ProcessSuspenderIntegrationTests
{
    private static string? FindFfmpeg()
    {
        var candidates = new[]
        {
            @"C:\Program Files\Jellyfin\Server\ffmpeg.exe",
            "/usr/lib/jellyfin-ffmpeg/ffmpeg",
            "/usr/bin/ffmpeg"
        };
        foreach (var c in candidates)
        {
            if (File.Exists(c))
            {
                return c;
            }
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var exe = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
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
            }
        }

        return null;
    }

    private static long Size(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;

    [Fact]
    public async Task Suspend_FreezesEncode_Resume_Continues()
    {
        var ffmpeg = FindFfmpeg();
        if (ffmpeg is null)
        {
            return; // no ffmpeg available — skip
        }

        var outPath = Path.Combine(Path.GetTempPath(), "pt-suspend-" + Path.GetRandomFileName() + ".mkv");
        var startInfo = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var a in new[]
        {
            "-y", "-nostdin", "-f", "lavfi", "-i", "testsrc2=size=1920x1080:rate=30",
            "-c:v", "libx264", "-preset", "fast", "-t", "600", outPath
        })
        {
            startInfo.ArgumentList.Add(a);
        }

        using var process = Process.Start(startInfo)!;
        process.ErrorDataReceived += (_, _) => { };
        process.OutputDataReceived += (_, _) => { };
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();

        try
        {
            // Wait until the encode is actually writing.
            var started = false;
            for (var i = 0; i < 60 && !started; i++)
            {
                await Task.Delay(250);
                started = Size(outPath) > 0;
            }

            Assert.True(started, "ffmpeg did not begin writing output");

            ProcessSuspender.Suspend(process);
            await Task.Delay(500); // let any in-flight write settle
            var s1 = Size(outPath);
            await Task.Delay(1500);
            var s2 = Size(outPath);
            Assert.True(s2 - s1 < 200_000, $"output kept growing while suspended ({s1} -> {s2})");

            ProcessSuspender.Resume(process);
            await Task.Delay(2000);
            var s3 = Size(outPath);
            Assert.True(s3 - s2 > 200_000, $"output did not resume growing after resume ({s2} -> {s3})");
        }
        finally
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
            }

            try
            {
                process.WaitForExit(5000);
            }
            catch (InvalidOperationException)
            {
            }

            try
            {
                File.Delete(outPath);
            }
            catch (IOException)
            {
            }
        }
    }
}
