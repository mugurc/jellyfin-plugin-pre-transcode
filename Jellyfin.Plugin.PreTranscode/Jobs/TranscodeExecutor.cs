using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PreTranscode.Configuration;
using Jellyfin.Plugin.PreTranscode.Encoding;
using Jellyfin.Plugin.PreTranscode.Ffmpeg;
using Jellyfin.Plugin.PreTranscode.Library;
using Jellyfin.Plugin.PreTranscode.Media;
using Jellyfin.Plugin.PreTranscode.Rules;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PreTranscode.Jobs;

/// <summary>
/// Processes a single <see cref="TranscodeJob"/> end to end: probe, skip-if-compliant, build the
/// command, run ffmpeg to a temp file, verify it, then apply the output policy. Never throws.
/// </summary>
internal sealed class TranscodeExecutor
{
    private readonly IJobQueue _queue;
    private readonly IMediaProber _prober;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly IFfmpegCapabilitiesService _capabilities;
    private readonly AlternateVersionMerger _merger;
    private readonly ReplacedItemUpdater _libraryUpdater;
    private readonly string _tempDirectory;
    private readonly ILogger<TranscodeExecutor> _logger;

    public TranscodeExecutor(
        IJobQueue queue,
        IMediaProber prober,
        IMediaEncoder mediaEncoder,
        IFfmpegCapabilitiesService capabilities,
        AlternateVersionMerger merger,
        ReplacedItemUpdater libraryUpdater,
        IApplicationPaths applicationPaths,
        ILogger<TranscodeExecutor> logger)
    {
        _queue = queue;
        _prober = prober;
        _mediaEncoder = mediaEncoder;
        _capabilities = capabilities;
        _merger = merger;
        _libraryUpdater = libraryUpdater;
        _tempDirectory = Path.Combine(applicationPaths.DataPath, "pretranscode", "tmp");
        _logger = logger;
    }

    /// <summary>
    /// Deletes whatever is left in the plugin's temp directory. Called once at startup, when by
    /// definition nothing is encoding, so everything there is debris.
    /// <para>
    /// Nothing else ever removed it. A cancel used to race the still-dying ffmpeg for the handle, a crash
    /// or a hard kill never got as far as the delete at all, and each one left a part-finished encode
    /// behind — routinely several gigabytes — inside Jellyfin's own data directory, for ever.
    /// </para>
    /// </summary>
    public void CleanTempDirectory()
    {
        try
        {
            if (!Directory.Exists(_tempDirectory))
            {
                return;
            }

            var freed = 0L;
            var count = 0;
            foreach (var file in Directory.EnumerateFiles(_tempDirectory))
            {
                try
                {
                    var size = new FileInfo(file).Length;
                    File.Delete(file);
                    freed += size;
                    count++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(ex, "Could not delete leftover temp file {Path}", file);
                }
            }

            if (count > 0)
            {
                _logger.LogInformation(
                    "Removed {Count} leftover pre-transcode temp file(s), freeing {MegaBytes} MB",
                    count,
                    freed / (1024 * 1024));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not sweep the pre-transcode temp directory");
        }
    }

    public async Task ExecuteAsync(TranscodeJob job, CancellationToken cancellationToken, Action<Process>? onProcessStarted = null)
    {
        var tempFile = string.Empty;
        try
        {
            var config = Plugin.Instance?.Configuration;
            if (config is null)
            {
                Fail(job, "plugin configuration unavailable");
                return;
            }

            var profile = ResolveProfile(config, job.ProfileId);
            if (profile is null)
            {
                Fail(job, "no encoding profile is configured");
                return;
            }

            if (!File.Exists(job.SourcePath))
            {
                Fail(job, "source file no longer exists");
                return;
            }

            // Re-check idempotency at execution time, not just at enqueue time. A source can legitimately
            // be queued more than once (a fresh library scan re-fires evaluation for every item, and in
            // alternate-version mode each completed job triggers a scan), and nothing prevents a stale
            // duplicate from reaching the worker. Without this the profile's output for that source is
            // produced a second time — MakeUnique names it "... (1)" — so the same episode is transcoded
            // again and a duplicate output piles up. If the expected output is already on disk, this job
            // is a no-op; skip it.
            // The evaluator refuses to queue a profile that would write its output over its own source,
            // but a manually-queued item ("Transcode a single item") never passes through the evaluator —
            // it goes straight into the queue — so the same guard has to exist on this path too. Without
            // it the applier falls back to a unique name and drops "Movie (1).mkv" next to the source,
            // which Jellyfin then indexes as a second movie, and the next such job makes "(2)". Failed
            // rather than Skipped: nothing was already done, the job simply cannot be carried out as
            // configured, and only the admin can resolve it.
            if (OutputApplier.WritesOverItsOwnSource(profile, job.SourcePath))
            {
                Fail(
                    job,
                    "profile '" + profile.Name + "' writes its output to the source's own path — choose "
                    + "'Replace in place', set an output directory, or pick a container different from the "
                    + "source's extension");
                return;
            }

            var expectedOutput = OutputApplier.ExpectedOutputPath(profile, job.SourcePath);
            if (expectedOutput is not null
                && !OutputApplier.IsSameFile(expectedOutput, job.SourcePath)
                && File.Exists(expectedOutput))
            {
                job.Status = JobStatus.Skipped;
                job.StatusDetail = "output already exists";
                job.OutputPath = expectedOutput;
                job.Progress = 100;
                job.FinishedUtc = DateTime.UtcNow;
                _queue.Update(job);
                _logger.LogInformation("Skipped {Path}: output already exists at {Output}", job.SourcePath, expectedOutput);
                return;
            }

            SetDetail(job, "probing");
            var probe = await _prober.ProbeAsync(job.SourcePath, cancellationToken).ConfigureAwait(false);
            if (probe is null)
            {
                Fail(job, "could not probe source file");
                return;
            }

            // Mirrors the evaluator: a profile that exists to re-encode material which already carries the
            // target codec (shrinking oversized H.265, say) can turn this off, because the compliance check
            // compares only codec/container/resolution/audio and would otherwise call every such file
            // compliant. A manually-queued item lands here without ever passing the evaluator, so this is
            // also the only compliance gate it sees.
            if (ProfileComplianceChecker.ShouldSkipAsCompliant(profile, probe, config.ResolutionPresets))
            {
                job.Status = JobStatus.Skipped;
                job.StatusDetail = "already compliant — size/bitrate are not compared; "
                    + "turn off “Skip files already matching this profile” to encode it anyway";
                job.Progress = 100;
                job.FinishedUtc = DateTime.UtcNow;
                _queue.Update(job);
                _logger.LogInformation("Skipped {Path}: already compliant with profile {Profile}", job.SourcePath, profile.Name);
                return;
            }

            Directory.CreateDirectory(_tempDirectory);
            tempFile = Path.Combine(_tempDirectory, job.Id + OutputApplier.ContainerExtension(profile.Container));

            // Ask ffmpeg itself which pixel formats this encoder takes, so a 10-bit source can keep its
            // bit depth only when the encoder actually accepts a 10-bit format (the correct one differs
            // per family). Cached after the first call; on failure the list is empty and the builder
            // falls back to 8-bit rather than guessing.
            var encoderPixelFormats = await GetEncoderPixelFormatsAsync(profile, cancellationToken).ConfigureAwait(false);

            var arguments = FfmpegCommandBuilder.BuildArguments(
                profile,
                probe,
                config.ResolutionPresets,
                job.SourcePath,
                tempFile,
                encoderPixelFormats,
                config.HardwareDecoder);
            _logger.LogInformation("Transcoding {Path} -> {Command}", job.SourcePath, FfmpegCommandBuilder.ToCommandLine(arguments));

            SetDetail(job, "transcoding");

            var (exitCode, stdErrTail) = await FfmpegExecutor.RunAsync(
                FfmpegPaths.ResolveFfmpeg(_mediaEncoder),
                arguments,
                probe.DurationSeconds,
                percent =>
                {
                    // Update progress in memory only. The queue hands out live job references, so the UI
                    // still sees the current percent on its next poll; persisting it would rewrite the
                    // whole queue file ~50x per transcode for a value that is discarded on restart anyway
                    // (a Processing job is reset to Pending on load).
                    job.Progress = percent;
                },
                cancellationToken,
                onProcessStarted).ConfigureAwait(false);

            if (exitCode != 0)
            {
                job.LogExcerpt = stdErrTail;
                Fail(job, "ffmpeg exited with code " + exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture));
                TryDelete(tempFile);
                return;
            }

            SetDetail(job, "verifying");
            var (ok, reason) = await OutputVerifier.VerifyAsync(_prober, tempFile, probe, cancellationToken).ConfigureAwait(false);
            if (!ok)
            {
                Fail(job, "output verification failed: " + reason);
                TryDelete(tempFile);
                return;
            }

            // Optional space-guard: if the finished transcode is not actually smaller than the source, the
            // re-encode reclaimed nothing (a more efficient codec can still produce a bigger file on some
            // content), so throw the output away and keep the original untouched. OutputPath is set to the
            // source — the file that is being kept — so the idempotency check treats this source as handled
            // and does not re-transcode it on every sweep just to discard the result again.
            if (profile.DiscardOutputIfLarger && OutputIsNotSmaller(job.SourcePath, tempFile, out var sourceBytes, out var outputBytes))
            {
                job.Status = JobStatus.Skipped;
                job.StatusDetail = FormattableString.Invariant(
                    $"kept original — transcode was not smaller ({outputBytes / (1024d * 1024d):F0} MB vs {sourceBytes / (1024d * 1024d):F0} MB source)");
                job.OutputPath = job.SourcePath;
                job.OutputSizeBytes = sourceBytes;
                job.Progress = 100;
                job.FinishedUtc = DateTime.UtcNow;
                _queue.Update(job);
                TryDelete(tempFile);
                _logger.LogInformation("Kept original for {Path}: output {OutputBytes} B not smaller than source {SourceBytes} B", job.SourcePath, outputBytes, sourceBytes);
                return;
            }

            var finalPath = OutputApplier.Apply(profile, job.SourcePath, tempFile);

            // The output's extension comes from the profile's container, so replacing Movie.mp4 under an
            // mkv profile writes Movie.mkv and deletes Movie.mp4 — a rename as far as Jellyfin is
            // concerned, leaving its database row pointing at a file that no longer exists and every
            // playback failing until some later library scan. Repoint the row before the job is reported
            // done, and on CancellationToken.None: the file has already been swapped, so leaving the
            // library pointing at a deleted path is not an acceptable outcome of cancelling.
            if (profile.OutputMode == OutputHandlingMode.ReplaceInPlace)
            {
                await _libraryUpdater.TryUpdateAsync(job.SourcePath, finalPath, CancellationToken.None).ConfigureAwait(false);
            }

            job.Status = JobStatus.Completed;
            job.Progress = 100;
            job.StatusDetail = string.Empty;
            job.OutputPath = finalPath;
            job.OutputSizeBytes = FileSizeOrZero(finalPath);
            job.FinishedUtc = DateTime.UtcNow;
            _queue.Update(job);
            _logger.LogInformation("Completed {Path} -> {Output}", job.SourcePath, finalPath);

            if (profile.OutputMode == OutputHandlingMode.AddAsAlternateVersion)
            {
                // Detached and on CancellationToken.None: registering the alternate version waits for a
                // library scan to index the new file, which can take minutes and must not hold the
                // transcode's concurrency slot or be cancelled when this job completes.
                _ = _merger.TryMergeAsync(job.SourcePath, finalPath, CancellationToken.None);
            }
        }
        catch (OperationCanceledException)
        {
            job.Status = JobStatus.Cancelled;
            job.StatusDetail = string.Empty;
            job.FinishedUtc = DateTime.UtcNow;
            _queue.Update(job);
            TryDelete(tempFile);
            _logger.LogInformation("Cancelled job for {Path}", job.SourcePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error processing {Path}", job.SourcePath);
            Fail(job, ex.Message);
            TryDelete(tempFile);
        }
    }

    // Never fails the job: an encoder whose formats cannot be discovered simply gets the safe 8-bit
    // default, which is what every build did before the pixel-format policy existed.
    private async Task<IReadOnlyList<string>> GetEncoderPixelFormatsAsync(EncodingProfile profile, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(profile.VideoEncoder))
        {
            return Array.Empty<string>();
        }

        try
        {
            var info = await _capabilities.GetEncoderPresetsAsync(profile.VideoEncoder, cancellationToken).ConfigureAwait(false);
            return info.PixelFormats;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not read the pixel formats supported by {Encoder}; defaulting to 8-bit", profile.VideoEncoder);
            return Array.Empty<string>();
        }
    }

    private static EncodingProfile? ResolveProfile(PluginConfiguration config, string profileId)
    {
        return config.Profiles.FirstOrDefault(p => string.Equals(p.Id, profileId, StringComparison.Ordinal))
            ?? config.Profiles.FirstOrDefault(p => string.Equals(p.Id, config.DefaultProfileId, StringComparison.Ordinal))
            ?? config.Profiles.FirstOrDefault();
    }

    private void SetDetail(TranscodeJob job, string detail)
    {
        job.StatusDetail = detail;
        _queue.Update(job);
    }

    private void Fail(TranscodeJob job, string message)
    {
        job.Status = JobStatus.Failed;
        job.StatusDetail = string.Empty;
        job.ErrorMessage = message;
        job.FinishedUtc = DateTime.UtcNow;
        _queue.Update(job);
        _logger.LogWarning("Job failed for {Path}: {Message}", job.SourcePath, message);
    }

    internal static long FileSizeOrZero(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    // True when the produced output is at least as large as the source (i.e. re-encoding saved nothing).
    // Only reports true when both sizes are positively readable; if either file cannot be measured the
    // guard is skipped and the verified transcode is kept, rather than silently discarding good work.
    private static bool OutputIsNotSmaller(string sourcePath, string outputPath, out long sourceBytes, out long outputBytes)
    {
        sourceBytes = 0;
        outputBytes = 0;
        try
        {
            sourceBytes = new FileInfo(sourcePath).Length;
            outputBytes = new FileInfo(outputPath).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        return sourceBytes > 0 && outputBytes >= sourceBytes;
    }

    // Catches both exception types, because this runs from inside the catch blocks of ExecuteAsync: an
    // UnauthorizedAccessException escaping here (a read-only temp file on Windows) would propagate out of
    // the handler, breaking the "never throws" contract and leaving the job stuck Processing.
    private void TryDelete(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not delete temp file {Path}", path);
        }
    }
}
