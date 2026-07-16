using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.PreTranscode.Configuration;
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
                Recursive = true,
                IsVirtualItem = false
            });

            var enqueued = 0;
            for (var i = 0; i < items.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await EvaluateAndEnqueueAsync(items[i], cancellationToken).ConfigureAwait(false))
                {
                    enqueued++;
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

    internal async Task<bool> EvaluateAndEnqueueAsync(BaseItem item, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || !config.Enabled)
        {
            return false;
        }

        var path = item.Path;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return false;
        }

        if (!IsStable(path, config.FileStabilitySeconds))
        {
            return false;
        }

        var (profile, rules, enabled) = ResolveForLibrary(config, item);
        if (!enabled || profile is null)
        {
            return false;
        }

        var probe = await _prober.ProbeAsync(path, cancellationToken).ConfigureAwait(false);
        if (probe is null)
        {
            return false;
        }

        if (!RuleEvaluator.ShouldProcess(rules, probe))
        {
            return false;
        }

        if (ProfileComplianceChecker.IsAlreadyCompliant(profile, probe, config.ResolutionPresets))
        {
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

        var added = _queue.Enqueue(job);
        if (added)
        {
            _logger.LogInformation("Queued {Path} using profile {Profile}", path, profile.Name);
        }

        return added;
    }

    private static bool IsStable(string path, int stabilitySeconds)
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
        catch (IOException)
        {
            return false;
        }
    }

    private (EncodingProfile? Profile, IReadOnlyList<TriggerRule> Rules, bool Enabled) ResolveForLibrary(PluginConfiguration config, BaseItem item)
    {
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
