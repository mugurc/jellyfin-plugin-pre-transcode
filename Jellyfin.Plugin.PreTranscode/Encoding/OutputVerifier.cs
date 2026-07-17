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
            // A percentage tolerance alone grows without bound on long files (2% of a 2-hour film is
            // 144s), so a badly truncated encode would still pass. Cap the absolute tolerance so a
            // grossly missing chunk is always caught. The cap is 60s rather than something tighter
            // because imprecise-duration containers (MPEG-TS/.ts recordings, some DVD rips) legitimately
            // report a header duration tens of seconds off from the true, accurately-measured output.
            var tolerance = Math.Min(Math.Max(2.0, expectedDurationSeconds * 0.02), 60.0);
            if (Math.Abs(probe.DurationSeconds - expectedDurationSeconds) > tolerance)
            {
                return (false, FormattableString.Invariant(
                    $"duration mismatch: expected ~{expectedDurationSeconds:F1}s, got {probe.DurationSeconds:F1}s"));
            }
        }
        else if (probe.DurationSeconds <= 0)
        {
            // The source duration was unknown, so no comparison was possible; still refuse an output that
            // itself reports no duration, which is the signature of an encode that produced only a header.
            return (false, "output has no readable duration");
        }

        return (true, string.Empty);
    }
}
