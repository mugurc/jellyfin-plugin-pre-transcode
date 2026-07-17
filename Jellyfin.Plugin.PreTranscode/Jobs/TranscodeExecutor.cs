using System;
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
    private readonly AlternateVersionMerger _merger;
    private readonly string _tempDirectory;
    private readonly ILogger<TranscodeExecutor> _logger;

    public TranscodeExecutor(
        IJobQueue queue,
        IMediaProber prober,
        IMediaEncoder mediaEncoder,
        AlternateVersionMerger merger,
        IApplicationPaths applicationPaths,
        ILogger<TranscodeExecutor> logger)
    {
        _queue = queue;
        _prober = prober;
        _mediaEncoder = mediaEncoder;
        _merger = merger;
        _tempDirectory = Path.Combine(applicationPaths.DataPath, "pretranscode", "tmp");
        _logger = logger;
    }

    public async Task ExecuteAsync(TranscodeJob job, CancellationToken cancellationToken)
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

            SetDetail(job, "probing");
            var probe = await _prober.ProbeAsync(job.SourcePath, cancellationToken).ConfigureAwait(false);
            if (probe is null)
            {
                Fail(job, "could not probe source file");
                return;
            }

            if (ProfileComplianceChecker.IsAlreadyCompliant(profile, probe, config.ResolutionPresets))
            {
                job.Status = JobStatus.Skipped;
                job.StatusDetail = "already compliant";
                job.Progress = 100;
                job.FinishedUtc = DateTime.UtcNow;
                _queue.Update(job);
                _logger.LogInformation("Skipped {Path}: already compliant with profile {Profile}", job.SourcePath, profile.Name);
                return;
            }

            Directory.CreateDirectory(_tempDirectory);
            tempFile = Path.Combine(_tempDirectory, job.Id + OutputApplier.ContainerExtension(profile.Container));

            var arguments = FfmpegCommandBuilder.BuildArguments(profile, probe, config.ResolutionPresets, job.SourcePath, tempFile);
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
                cancellationToken).ConfigureAwait(false);

            if (exitCode != 0)
            {
                job.LogExcerpt = stdErrTail;
                Fail(job, "ffmpeg exited with code " + exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture));
                TryDelete(tempFile);
                return;
            }

            SetDetail(job, "verifying");
            var (ok, reason) = await OutputVerifier.VerifyAsync(_prober, tempFile, probe.DurationSeconds, cancellationToken).ConfigureAwait(false);
            if (!ok)
            {
                Fail(job, "output verification failed: " + reason);
                TryDelete(tempFile);
                return;
            }

            var finalPath = OutputApplier.Apply(profile, job.SourcePath, tempFile);

            job.Status = JobStatus.Completed;
            job.Progress = 100;
            job.StatusDetail = string.Empty;
            job.OutputPath = finalPath;
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
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Could not delete temp file {Path}", path);
        }
    }
}
