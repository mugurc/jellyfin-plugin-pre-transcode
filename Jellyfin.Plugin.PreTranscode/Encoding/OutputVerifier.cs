using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PreTranscode.Media;

namespace Jellyfin.Plugin.PreTranscode.Encoding;

/// <summary>
/// Verifies a freshly-produced output file before it is allowed to replace or accompany the source: it
/// must exist, be non-empty, be ffprobe-parseable, run for about as long as the source, and carry the
/// same shape of content — a picture of the same proportions, and no fewer audio tracks.
/// </summary>
internal static class OutputVerifier
{
    public static async Task<(bool Ok, string Reason)> VerifyAsync(
        IMediaProber prober,
        string outputPath,
        MediaProbeInfo source,
        CancellationToken cancellationToken)
    {
        var expectedDurationSeconds = source.DurationSeconds;

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

        return VerifyStructure(source, probe);
    }

    // Duration on its own is not evidence that the right thing was encoded: it is satisfied by the AUDIO
    // track alone. An output whose video track was a 600x900 cover-art thumbnail — which is what mapping
    // "0:v:0" produced on any file carrying embedded art — therefore sailed through, and under Replace in
    // place the original film was deleted for it. The stream map is fixed, but this is the gate that was
    // supposed to catch it.
    private static (bool Ok, string Reason) VerifyStructure(MediaProbeInfo source, MediaProbeInfo output)
    {
        // Only judged against what the source actually had. A source the prober found no video in is not
        // evidence of anything, and must not start failing encodes that worked before.
        if (!string.IsNullOrEmpty(source.VideoCodec) && source.Width > 0 && source.Height > 0)
        {
            if (string.IsNullOrEmpty(output.VideoCodec) || output.Width <= 0 || output.Height <= 0)
            {
                return (false, "output has no usable video stream");
            }

            // Scaling changes a picture's size, never its shape. The 15% band absorbs even-dimension
            // rounding and anamorphic sources; what it is here to catch — portrait cover art standing in
            // for a landscape film — is out by a factor of two or more.
            var sourceAspect = source.Width / (double)source.Height;
            var outputAspect = output.Width / (double)output.Height;
            if (Math.Abs(outputAspect - sourceAspect) > sourceAspect * 0.15)
            {
                return (false, FormattableString.Invariant(
                    $"output shape does not match the source: {output.Width}x{output.Height} encoded from {source.Width}x{source.Height}"));
            }
        }

        // Every audio track is mapped, so ending up with fewer means one failed to encode and ffmpeg
        // carried on regardless — the file plays, just without the track someone wanted.
        if (output.AudioStreams.Count < source.AudioStreams.Count)
        {
            return (false, FormattableString.Invariant(
                $"output has {output.AudioStreams.Count} audio track(s), the source has {source.AudioStreams.Count}"));
        }

        return (true, string.Empty);
    }
}
