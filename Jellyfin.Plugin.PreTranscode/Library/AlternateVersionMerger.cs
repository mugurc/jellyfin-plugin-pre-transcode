using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PreTranscode.Library;

/// <summary>
/// Registers a freshly-produced transcode as a Jellyfin <em>alternate version</em> of its source, the
/// same way the dashboard's "Merge Versions" does: a database link (<c>SetPrimaryVersionId</c> +
/// <c>LinkedAlternateVersions</c>), <b>not</b> a filename/folder convention. So the original file keeps
/// its name and only needs to co-exist; the two show up as one movie with a version selector.
/// Best-effort and never throws: if the output cannot be indexed/linked, the file is still on disk and
/// can be merged manually.
/// </summary>
internal sealed class AlternateVersionMerger
{
    // A library scan must index the new file before it exists as an item we can link. Give realtime
    // monitoring a short head start, then force a scan, then poll until it appears (bounded).
    private static readonly TimeSpan RealtimeGracePeriod = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan FindTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<AlternateVersionMerger> _logger;

    public AlternateVersionMerger(ILibraryManager libraryManager, ILogger<AlternateVersionMerger> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <summary>
    /// Links <paramref name="outputPath"/> to the item at <paramref name="sourcePath"/> as an alternate
    /// version, keeping the source as the primary. Never throws.
    /// </summary>
    /// <param name="sourcePath">The original source file path (kept as the primary version).</param>
    /// <param name="outputPath">The transcoded output path to attach as an alternate version.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task TryMergeAsync(string sourcePath, string outputPath, CancellationToken cancellationToken)
    {
        try
        {
            if (_libraryManager.FindByPath(sourcePath, false) is not Video primary)
            {
                _logger.LogWarning("Auto-merge skipped: source {Path} is not a known Jellyfin video item", sourcePath);
                return;
            }

            var alternate = await WaitForOutputItemAsync(outputPath, cancellationToken).ConfigureAwait(false);
            if (alternate is null)
            {
                _logger.LogWarning(
                    "Auto-merge skipped: transcoded output {Path} did not appear as a library item within {Timeout}; it can still be merged manually",
                    outputPath,
                    FindTimeout);
                return;
            }

            if (alternate.Id.Equals(primary.Id))
            {
                // Jellyfin already treats them as one item (e.g. matching version filenames).
                return;
            }

            MergeInto(primary, alternate);
            await alternate.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
            await primary.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation("Registered {Output} as an alternate version of {Source}", outputPath, sourcePath);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Auto-merge of alternate version failed for {Output}", outputPath);
        }
    }

    // Mirrors the dashboard's MergeVersions logic, but with the source pinned as the primary version.
    private static void MergeInto(Video primary, Video alternate)
    {
        var alternates = primary.LinkedAlternateVersions.ToList();

        alternate.SetPrimaryVersionId(primary.Id.ToString("N", CultureInfo.InvariantCulture));

        if (!alternates.Any(l => string.Equals(l.Path, alternate.Path, StringComparison.OrdinalIgnoreCase)))
        {
            alternates.Add(new LinkedChild { Path = alternate.Path, ItemId = alternate.Id });
        }

        // Absorb any alternates the output item itself carried (normally none), then clear its own list.
        foreach (var linked in alternate.LinkedAlternateVersions)
        {
            if (!alternates.Any(l => string.Equals(l.Path, linked.Path, StringComparison.OrdinalIgnoreCase)))
            {
                alternates.Add(linked);
            }
        }

        alternate.LinkedAlternateVersions = Array.Empty<LinkedChild>();
        primary.LinkedAlternateVersions = alternates.ToArray();
    }

    private async Task<Video?> WaitForOutputItemAsync(string outputPath, CancellationToken cancellationToken)
    {
        // Maybe realtime monitoring already indexed it.
        if (_libraryManager.FindByPath(outputPath, false) is Video already)
        {
            return already;
        }

        await Task.Delay(RealtimeGracePeriod, cancellationToken).ConfigureAwait(false);
        if (_libraryManager.FindByPath(outputPath, false) is Video afterGrace)
        {
            return afterGrace;
        }

        _logger.LogInformation("Requesting a library scan so {Path} can be indexed and merged", outputPath);
        _libraryManager.QueueLibraryScan();

        for (var waited = TimeSpan.Zero; waited < FindTimeout; waited += PollInterval)
        {
            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
            if (_libraryManager.FindByPath(outputPath, false) is Video found)
            {
                return found;
            }
        }

        return null;
    }
}
