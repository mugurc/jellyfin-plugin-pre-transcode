using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PreTranscode.Media;

namespace Jellyfin.Plugin.PreTranscode.Encoding;

/// <summary>
/// Verifies a freshly-produced output file before it is allowed to replace or accompany the source:
/// it must exist, be non-empty, be ffprobe-parseable and have a duration close to the source.
/// </summary>
internal static class OutputVerifier
{
    public static async Task<(bool Ok, string Reason)> VerifyAsync(
        IMediaProber prober,
        string outputPath,
        double expectedDurationSeconds,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(outputPath))
        {
            return (false, "output file was not created");
        }

        if (new FileInfo(outputPath).Length <= 0)
        {
            return (false, "output file is empty");
        }

        var probe = await prober.ProbeAsync(outputPath, cancellationToken).ConfigureAwait(false);
        if (probe is null)
        {
            return (false, "output is not ffprobe-parseable");
        }

        if (expectedDurationSeconds > 0)
        {
            var tolerance = Math.Max(2.0, expectedDurationSeconds * 0.02);
            if (Math.Abs(probe.DurationSeconds - expectedDurationSeconds) > tolerance)
            {
                return (false, FormattableString.Invariant(
                    $"duration mismatch: expected ~{expectedDurationSeconds:F1}s, got {probe.DurationSeconds:F1}s"));
            }
        }

        return (true, string.Empty);
    }
}
