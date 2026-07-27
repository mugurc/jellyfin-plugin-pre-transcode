using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.PreTranscode.Configuration;
using Jellyfin.Plugin.PreTranscode.Encoding;
using Jellyfin.Plugin.PreTranscode.Jobs;
using Jellyfin.Plugin.PreTranscode.Media;
using Jellyfin.Plugin.PreTranscode.Rules;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PreTranscode.Library;

/// <summary>
/// Shared logic that evaluates a Jellyfin library item against the active rules for its library and,
/// if it matches (and is not already compliant), enqueues a pre-transcode job. Used by the library
/// scan hook, the scheduled sweep and the item-added monitor.
/// </summary>
public sealed class ItemEvaluator
{
    // After this many failed attempts for the same source+profile, stop auto-retrying it on every
    // sweep; the admin can still Requeue it manually from the queue page.
    private const int MaxAutoFailedAttempts = 3;

    private readonly IJobQueue _queue;
    private readonly IMediaProber _prober;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<ItemEvaluator> _logger;

    private int _sweepRunning;

    /// <summary>
    /// Initializes a new instance of the <see cref="ItemEvaluator"/> class.
    /// </summary>
    /// <param name="queue">The job queue.</param>
    /// <param name="prober">The media prober.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="logger">The logger.</param>
    public ItemEvaluator(IJobQueue queue, IMediaProber prober, ILibraryManager libraryManager, ILogger<ItemEvaluator> logger)
    {
        _queue = queue;
        _prober = prober;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    // Only one sweep runs at a time: the scheduled task, the post-scan hook and the dashboard's
    // "Scan library now" button can all land at once, and each sweep ffprobes every item.
    internal async Task<int> SweepAsync(IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _sweepRunning, 1, 0) == 1)
        {
            _logger.LogInformation("A Pre-Transcode sweep is already running; skipping this one");
            progress?.Report(100);
            return 0;
        }

        try
        {
            var config = Plugin.Instance?.Configuration;
            if (config is null || !config.Enabled)
            {
                progress?.Report(100);
                return 0;
            }

            var items = _libraryManager.GetItemList(new InternalItemsQuery
            {
                MediaTypes = new[] { MediaType.Video },
                SourceTypes = new[] { SourceType.Library },
                IsFolder = false,
                Recursive = true,
                IsVirtualItem = false
            });

            // Snapshot the job list once for the whole sweep. The per-item pre-check only needs it to
            // skip already-handled sources cheaply; the race-free final decision is still made live under
            // the queue lock inside Enqueue. Calling GetJobs() per item copied the entire (never-pruned)
            // list N times — O(items x jobs).
            var knownJobs = _queue.GetJobs();

            var enqueued = 0;
            for (var i = 0; i < items.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Guard each item so one unexpected failure (an odd path, a transient library error) skips
                // just that item instead of aborting the sweep and leaving every later item unevaluated.
                try
                {
                    if (await EvaluateAndEnqueueAsync(items[i], cancellationToken, knownJobs).ConfigureAwait(false))
                    {
                        enqueued++;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Skipping {Path}: evaluation failed", items[i]?.Path);
                }

                progress?.Report((i + 1) * 100.0 / Math.Max(1, items.Count));
            }

            _logger.LogInformation("Pre-Transcode sweep queued {Count} job(s) from {Total} item(s)", enqueued, items.Count);
            return enqueued;
        }
        finally
        {
            Volatile.Write(ref _sweepRunning, 0);
        }
    }

    internal async Task<bool> EvaluateAndEnqueueAsync(BaseItem item, CancellationToken cancellationToken, IReadOnlyList<TranscodeJob>? knownJobs = null)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || !config.Enabled)
        {
            return false;
        }

        // Every exit below is a decision the admin cannot otherwise observe — the method just returns
        // false, so a rule that never fires looks identical to a file the plugin never saw. With the flag
        // on, each one says which check stood the item down and what it was comparing.
        var verbose = config.VerboseRuleLogging;

        var path = item.Path;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            Explain(verbose, path, "the library item has no existing file on disk");
            return false;
        }

        if (!IsStable(path, config.FileStabilitySeconds))
        {
            Explain(verbose, path, FormattableString.Invariant(
                $"the file was written less than {config.FileStabilitySeconds}s ago and is still settling (FileStabilitySeconds)"));
            return false;
        }

        var (profile, rules, enabled) = ResolveForLibrary(config, item);
        if (!enabled)
        {
            Explain(verbose, path, "this item's library override is switched off");
            return false;
        }

        if (profile is null)
        {
            Explain(verbose, path, "no encoding profile is configured");
            return false;
        }

        // A "separate directory" profile can resolve its output to the source file itself: no output
        // directory is set (so the output lands beside the source) and the profile's container maps to
        // the extension the source already has. The applier cannot overwrite the source in this mode, so
        // it falls back to a unique name and writes "Movie (1).mkv" next to it — which Jellyfin indexes
        // as its own library item, the next sweep evaluates as a fresh source, and transcodes into
        // "Movie (1) (1).mkv". One more re-encoded generation per sweep, forever. Warn (not just under
        // verbose): this is a misconfiguration no admin can have intended, and the fix is theirs to make.
        if (WritesOverItsOwnSource(profile, path))
        {
            _logger.LogWarning(
                "Skipping {Path}: profile '{Profile}' writes its output to the source's own path. Choose "
                + "'Replace in place', set an output directory, or pick a container different from the "
                + "source's extension — otherwise each sweep would add another re-encoded '(1)' copy.",
                path,
                profile.Name);
            return false;
        }

        // Idempotency across sweeps. The compliance check further down only prevents a repeat for
        // Replace-in-place (where the source itself becomes compliant); the separate-directory and
        // alternate-version modes leave the source unchanged, so without this it would be re-transcoded
        // on every daily sweep, piling up duplicate outputs. Skip when either (a) the expected output
        // for this profile already exists on disk (robust — survives clearing the queue), or (b) the
        // queue records a completed transcode for it, or it has failed too many times to keep retrying.
        if (OutputAlreadyExists(profile, path))
        {
            Explain(verbose, path, FormattableString.Invariant(
                $"this profile's output already exists at '{OutputApplier.ExpectedOutputPath(profile, path)}'"));
            return false;
        }

        if (AlreadyHandled(knownJobs ?? _queue.GetJobs(), path, profile.Id, File.Exists, MaxAutoFailedAttempts))
        {
            Explain(verbose, path, FormattableString.Invariant(
                $"the queue already records a finished transcode of this source for profile '{profile.Name}', or it has failed {MaxAutoFailedAttempts} times (requeue it manually from the queue page)"));
            return false;
        }

        var probe = await _prober.ProbeAsync(path, cancellationToken).ConfigureAwait(false);
        if (probe is null)
        {
            Explain(verbose, path, "ffprobe could not read the file (see the preceding ffprobe warning)");
            return false;
        }

        var matched = RuleEvaluator.ShouldProcess(rules, probe);
        if (verbose)
        {
            _logger.LogInformation(
                "Pre-Transcode evaluation of {Path}:\n  {Media}\n{Rules}",
                path,
                RuleTrace.DescribeMedia(probe),
                RuleTrace.DescribeRules(rules, probe));
        }

        if (!matched)
        {
            return false;
        }

        // The rules said yes; this check can still say no. It compares only codec, container, resolution,
        // audio codec/channels and HDR — it has no notion of file size or bitrate — so a profile whose
        // whole purpose is to shrink material that ALREADY has the target codec would find every such
        // file "compliant" and veto the rule that just matched. SkipIfAlreadyCompliant lets those
        // profiles opt out; it stays on by default, which is what keeps ordinary sweeps idempotent.
        if (ProfileComplianceChecker.ShouldSkipAsCompliant(profile, probe, config.ResolutionPresets))
        {
            Explain(
                verbose,
                path,
                "a rule matched, but the file is already compliant with the target profile, so transcoding "
                + "it would change nothing this profile compares — "
                + RuleTrace.DescribeCompliance(profile, probe, config.ResolutionPresets)
                + ". Note that the compliance check ignores file size and bitrate; if this profile exists to "
                + "re-encode material that already has the target codec, turn off 'Skip files already matching "
                + "this profile' on it.");
            return false;
        }

        var job = new TranscodeJob
        {
            SourcePath = path,
            ProfileId = profile.Id,
            ItemId = item.Id.ToString("N"),
            DisplayName = string.IsNullOrEmpty(item.Name) ? Path.GetFileName(path) : item.Name,
            CreatedUtc = DateTime.UtcNow
        };

        // Re-run the idempotency checks atomically with the add. The checks above ran before the ffprobe
        // await, so between them and here an earlier job for this same source can have completed and
        // written its output; without this a second (duplicate) job would be enqueued and later
        // re-transcoded into a "... (1)" file. Evaluated under the queue lock, with the live job list.
        var added = _queue.Enqueue(job, jobs =>
            OutputAlreadyExists(profile, path)
            || AlreadyHandled(jobs, path, profile.Id, File.Exists, MaxAutoFailedAttempts));
        if (added)
        {
            _logger.LogInformation("Queued {Path} using profile {Profile}", path, profile.Name);
        }
        else
        {
            // The queue admits one active job per SOURCE, not per source+profile. That is deliberate:
            // two profiles encoding the same file at once would race, and with Replace-in-place one would
            // delete the source out from under the other. So a second profile's job simply waits for the
            // first to finish and is picked up by the next sweep — a delay, not a loss.
            Explain(verbose, path, FormattableString.Invariant(
                $"an active job already exists for this source (only one at a time, across all profiles); profile '{profile.Name}' will be re-evaluated on the next sweep"));
        }

        return added;
    }

    // Information level, not Debug: the whole point is that an admin can switch this on from the plugin
    // page and read the answer in the normal Jellyfin log, without lowering the server's global log level.
    private void Explain(bool verbose, string? path, string reason)
    {
        if (verbose)
        {
            _logger.LogInformation("Pre-Transcode skipped {Path}: {Reason}", string.IsNullOrEmpty(path) ? "(item with no path)" : path, reason);
        }
    }

    // A file is "stable" once its last-write time is at least stabilitySeconds in the past — a guard
    // against grabbing an in-progress download/copy. Shared with LibraryMonitor so a newly-added item
    // that is not stable yet can be deferred and re-checked rather than skipped outright.
    internal static bool IsStable(string path, int stabilitySeconds)
    {
        if (stabilitySeconds <= 0)
        {
            return true;
        }

        try
        {
            var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(path);
            return age.TotalSeconds >= stabilitySeconds;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Treat an unreadable/odd path as "not settled yet" and skip it, rather than letting the
            // exception escape and abort the whole sweep loop. (IOException alone missed these siblings.)
            return false;
        }
    }

    // Queue-independent: true when the profile's expected output for this source already exists on disk
    // (a distinct file, not the source itself). Survives clearing the job queue.
    private static bool OutputAlreadyExists(EncodingProfile profile, string sourcePath)
    {
        var expected = OutputApplier.ExpectedOutputPath(profile, sourcePath);
        return expected is not null
            && !OutputApplier.IsSameFile(expected, sourcePath)
            && File.Exists(expected);
    }

    // Delegates to the single definition shared with the executor, which a manually-queued job reaches
    // without ever passing through this evaluator.
    internal static bool WritesOverItsOwnSource(EncodingProfile profile, string sourcePath)
    {
        return OutputApplier.WritesOverItsOwnSource(profile, sourcePath);
    }

    // True when this source should not be auto-queued again for this profile: it either already has a
    // resolved transcode whose recorded output still exists, or it has failed at least maxFailedAttempts
    // times. A Completed job records its produced output; a Skipped job records an existing file it stood
    // down for — the pre-existing output it found, or (with DiscardOutputIfLarger) the original it kept
    // because the transcode was not smaller. Either way the source is handled and must not be re-queued
    // and re-transcoded on every sweep. Pure over the job list (outputExists is injected) so it is unit-testable.
    internal static bool AlreadyHandled(
        IEnumerable<TranscodeJob> jobs,
        string sourcePath,
        string profileId,
        Func<string, bool> outputExists,
        int maxFailedAttempts)
    {
        var failed = 0;
        foreach (var job in jobs)
        {
            if (!string.Equals(job.SourcePath, sourcePath, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(job.ProfileId, profileId, StringComparison.Ordinal))
            {
                continue;
            }

            if ((job.Status == JobStatus.Completed || job.Status == JobStatus.Skipped)
                && !string.IsNullOrEmpty(job.OutputPath)
                && outputExists(job.OutputPath))
            {
                return true;
            }

            if (job.Status == JobStatus.Failed)
            {
                failed++;
            }
        }

        return maxFailedAttempts > 0 && failed >= maxFailedAttempts;
    }

    private (EncodingProfile? Profile, IReadOnlyList<TriggerRule> Rules, bool Enabled) ResolveForLibrary(PluginConfiguration config, BaseItem item)
    {
        // No overrides configured: skip the per-item collection-folder lookup entirely and go straight to
        // the default profile + global rules. This runs for every item in a sweep, so avoiding the
        // library query when it can never match a thing matters on large libraries.
        if (config.LibraryOverrides.Count == 0)
        {
            return (DefaultProfile(config), config.GlobalRules, true);
        }

        LibraryOverride? found = null;
        try
        {
            foreach (var folder in _libraryManager.GetCollectionFolders(item))
            {
                var folderId = folder.Id.ToString("N");
                found = config.LibraryOverrides.FirstOrDefault(o => string.Equals(NormalizeGuid(o.LibraryId), folderId, StringComparison.Ordinal));
                if (found is not null)
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not resolve collection folders for {Path}", item.Path);
        }

        if (found is not null)
        {
            if (!found.Enabled)
            {
                return (null, Array.Empty<TriggerRule>(), false);
            }

            var overrideProfile = config.Profiles.FirstOrDefault(p => string.Equals(p.Id, found.ProfileId, StringComparison.Ordinal)) ?? DefaultProfile(config);
            var overrideRules = found.UseGlobalRules ? config.GlobalRules : found.Rules;
            return (overrideProfile, overrideRules, true);
        }

        return (DefaultProfile(config), config.GlobalRules, true);
    }

    private static EncodingProfile? DefaultProfile(PluginConfiguration config)
    {
        return config.Profiles.FirstOrDefault(p => string.Equals(p.Id, config.DefaultProfileId, StringComparison.Ordinal))
            ?? config.Profiles.FirstOrDefault();
    }

    internal static string NormalizeGuid(string value)
    {
        return Guid.TryParse(value, out var guid)
            ? guid.ToString("N")
            : (value ?? string.Empty).Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
    }
}
