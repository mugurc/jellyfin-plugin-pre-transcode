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
    /// Gets or sets how long a job whose ffmpeg or ffprobe was killed by a signal waits for the server
    /// to stop before the death is treated as a failure. See <see cref="WaitOutInterruptionAsync"/>.
    /// </summary>
    internal TimeSpan InterruptionGrace { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Gets a value indicating whether Jellyfin has told us where its ffmpeg is. Until it has,
    /// <see cref="FfmpegPaths"/> can only fall back to whatever "ffmpeg" is on PATH.
    /// </summary>
    internal bool FfmpegPathKnown => !string.IsNullOrWhiteSpace(_mediaEncoder.EncoderPath);

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
                Fail(
                    job,
                    string.IsNullOrWhiteSpace(job.ProfileId)
                        ? "no encoding profile is configured"
                        : "the encoding profile this job was queued with (id " + job.ProfileId + ") no longer exists; "
                            + "queue the item again to use one of the current profiles");
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
                Finish(job, JobStatus.Skipped, j =>
                {
                    j.StatusDetail = "output already exists";
                    j.OutputPath = expectedOutput;
                    j.Progress = 100;
                });
                _logger.LogInformation("Skipped {Path}: output already exists at {Output}", job.SourcePath, expectedOutput);
                return;
            }

            // A destination the apply step cannot write (read-only mount, missing permission, a name over
            // the filesystem's limit, an extension Jellyfin would not index) used to be discovered only
            // after the whole encode, and then again on each retry. Checked here, before ffmpeg starts.
            var destinationProblem = OutputApplier.Preflight(profile, job.SourcePath, _logger);
            if (destinationProblem is not null)
            {
                Fail(job, destinationProblem);
                return;
            }

            SetDetail(job, "probing");

            // What the source looked like when the encode began; compared again before the result is used.
            var sourceAtStart = SourceFingerprint.Of(job.SourcePath);
            MediaProbeInfo? probe;
            using (var signals = ProcessRunner.WatchSignals())
            {
                probe = await _prober.ProbeAsync(job.SourcePath, cancellationToken).ConfigureAwait(false);
                if (probe is null)
                {
                    await WaitOutInterruptionAsync(job, "ffprobe", signals.Signal, cancellationToken).ConfigureAwait(false);
                }
            }

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
                Finish(job, JobStatus.Skipped, j =>
                {
                    j.StatusDetail = "already compliant — size/bitrate are not compared; "
                        + "turn off “Skip files already matching this profile” to encode it anyway";
                    j.Progress = 100;
                });
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
                var signal = FfmpegExecutor.ReceivedSignal(stdErrTail);
                await WaitOutInterruptionAsync(job, "ffmpeg", signal, cancellationToken).ConfigureAwait(false);
                job.LogExcerpt = stdErrTail;
                Fail(
                    job,
                    "ffmpeg exited with code " + exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + (signal is null ? string.Empty : FormattableString.Invariant($" after receiving signal {signal}, and the server did not stop")));
                TryDelete(tempFile);
                return;
            }

            SetDetail(job, "verifying");
            bool ok;
            string reason;
            using (var signals = ProcessRunner.WatchSignals())
            {
                (ok, reason) = await OutputVerifier.VerifyAsync(_prober, tempFile, probe, cancellationToken).ConfigureAwait(false);
                if (!ok)
                {
                    await WaitOutInterruptionAsync(job, "ffprobe", signals.Signal, cancellationToken).ConfigureAwait(false);
                }
            }

            if (!ok)
            {
                Fail(job, "output verification failed: " + reason);
                TryDelete(tempFile);
                return;
            }

            if (DiscardIfSourceChanged(job, sourceAtStart, tempFile))
            {
                return;
            }

            // Optional space-guard: if the finished transcode is not actually smaller than the source, the
            // re-encode reclaimed nothing (a more efficient codec can still produce a bigger file on some
            // content), so throw the output away and keep the original untouched. OutputPath is set to the
            // source — the file that is being kept — so the idempotency check treats this source as handled
            // and does not re-transcode it on every sweep just to discard the result again.
            if (profile.DiscardOutputIfLarger && OutputIsNotSmaller(job.SourcePath, tempFile, out var sourceBytes, out var outputBytes))
            {
                Finish(job, JobStatus.Skipped, j =>
                {
                    j.StatusDetail = FormattableString.Invariant(
                        $"kept original — transcode was not smaller ({outputBytes / (1024d * 1024d):F0} MB vs {sourceBytes / (1024d * 1024d):F0} MB source)");
                    j.OutputPath = j.SourcePath;
                    j.OutputSizeBytes = sourceBytes;
                    j.Progress = 100;
                });
                TryDelete(tempFile);
                _logger.LogInformation("Kept original for {Path}: output {OutputBytes} B not smaller than source {SourceBytes} B", job.SourcePath, outputBytes, sourceBytes);
                return;
            }

            // And again as late as possible, right before the source is touched.
            if (DiscardIfSourceChanged(job, sourceAtStart, tempFile))
            {
                return;
            }

            var finalPath = OutputApplier.Apply(profile, job.SourcePath, tempFile, _logger);

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

            var finalBytes = FileSizeOrZero(finalPath);
            Finish(job, JobStatus.Completed, j =>
            {
                j.Progress = 100;
                j.StatusDetail = string.Empty;
                j.OutputPath = finalPath;
                j.OutputSizeBytes = finalBytes;
            });
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
            Finish(job, JobStatus.Cancelled, j => j.StatusDetail = string.Empty);
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

    // Only a job queued without a profile of its own runs under the default. One whose profile has since
    // been deleted used to fall through to the default too, so a job queued for a separate-directory
    // profile could end up replacing the original under a replace-in-place default. There is no profile
    // left for it to run under, and guessing one is not safe.
    internal static EncodingProfile? ResolveProfile(PluginConfiguration config, string profileId)
    {
        if (!string.IsNullOrWhiteSpace(profileId))
        {
            return config.Profiles.FirstOrDefault(p => string.Equals(p.Id, profileId, StringComparison.Ordinal));
        }

        return config.Profiles.FirstOrDefault(p => string.Equals(p.Id, config.DefaultProfileId, StringComparison.Ordinal))
            ?? config.Profiles.FirstOrDefault();
    }

    private void SetDetail(TranscodeJob job, string detail)
    {
        job.StatusDetail = detail;
        _queue.Update(job);
    }

    private void Fail(TranscodeJob job, string message)
    {
        Finish(job, JobStatus.Failed, j =>
        {
            j.StatusDetail = string.Empty;
            j.ErrorMessage = message;
        });
        _logger.LogWarning("Job failed for {Path}: {Message}", job.SourcePath, message);
    }

    // Every final state goes through here, applied under the queue's lock (see IJobQueue.Update). Setting
    // the fields one by one on the live job let a concurrent enqueue's history trim see it Completed with
    // no finish time yet, drop it as the oldest record, and leave the update that followed nothing to
    // save: the Completed record was lost and the next sweep re-encoded the file.
    private void Finish(TranscodeJob job, JobStatus status, Action<TranscodeJob> change)
    {
        var finished = DateTime.UtcNow;
        _queue.Update(job, j =>
        {
            change(j);
            j.FinishedUtc = finished;
            j.Status = status;
        });
    }

    // The source is re-read here rather than trusted from the start of the job: an *arr importing an
    // upgrade under the same name while the old release encodes would otherwise have that upgrade
    // overwritten by an encode of the old one (same container) or deleted in favour of it (Movie.mkv ->
    // Movie.mp4), a deleted item would be brought back, and an alternate version of old content would be
    // attached to the new file. Skipped rather than Failed, and with no output recorded: nothing is wrong
    // with the new file, and a failure would count towards the limit that stops a file being queued
    // automatically, while a Skipped job with no output does not mark the source as handled — so the
    // next sweep evaluates the new file like any other.
    private bool DiscardIfSourceChanged(TranscodeJob job, SourceFingerprint atStart, string tempFile)
    {
        var now = SourceFingerprint.Of(job.SourcePath);
        if (now == atStart)
        {
            return false;
        }

        var detail = now.Exists
            ? "source changed during the encode; output discarded, original left untouched"
            : "source was deleted during the encode; output discarded";
        Finish(job, JobStatus.Skipped, j =>
        {
            j.StatusDetail = detail;
            j.OutputPath = string.Empty;
            j.OutputSizeBytes = 0;
            j.Progress = 100;
        });
        TryDelete(tempFile);
        _logger.LogWarning("Discarded the encode of {Path}: {Detail}", job.SourcePath, detail);
        return true;
    }

    // A child killed by a signal while the server keeps running was killed by someone; one killed because
    // the server is stopping is a restart, and recording that as a failure meant three restarts stopped
    // the file from ever being queued automatically again. The two cannot be told apart when it happens:
    // systemd's default KillMode signals every process in the service, and Ctrl+C the whole process group,
    // so ffmpeg is told to stop before the host has even begun stopping the queue. So wait for the stop. If
    // it comes, its cancellation takes the job down the ordinary cancel path, which puts it back to
    // Pending. If it does not, the caller records the failure as before rather than requeueing, which
    // would loop for ever on a file whose encode is always killed (a CPU-time limit, a watchdog).
    private async Task WaitOutInterruptionAsync(TranscodeJob job, string program, int? signal, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (signal is null)
        {
            return;
        }

        _logger.LogInformation(
            "{Program} for {Path} was stopped by signal {Signal}; waiting up to {Seconds} s for the server to stop before recording a failure",
            program,
            job.SourcePath,
            signal,
            InterruptionGrace.TotalSeconds);
        SetDetail(job, FormattableString.Invariant($"{program} was stopped by signal {signal}; waiting to see whether the server is stopping"));
        await Task.Delay(InterruptionGrace, cancellationToken).ConfigureAwait(false);
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
