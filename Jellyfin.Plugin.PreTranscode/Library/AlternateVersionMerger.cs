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
internal sealed class AlternateVersionMerger : IDisposable
{
    // How long to wait for the newly-written file to be indexed as a library item before giving up.
    private static readonly TimeSpan FindTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<AlternateVersionMerger> _logger;

    // Merges are fired detached, one per completed episode. Running them concurrently — each finding and
    // rewriting items while a library scan re-indexes the very same new files — is what left some
    // episodes as unmerged duplicates (a last-writer-wins race with no locking on the DB row). Doing
    // them strictly one at a time removes that race.
    private readonly SemaphoreSlim _mergeLock = new(1, 1);

    public AlternateVersionMerger(ILibraryManager libraryManager, ILogger<AlternateVersionMerger> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    public void Dispose()
    {
        _mergeLock.Dispose();
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
            // Wait for the freshly-written output to be indexed BEFORE taking the merge lock. Holding the
            // single lock across this up-to-5-minute poll would serialize every completed episode behind
            // one slow index — a large overnight backlog of parked, uncancellable tasks each stalling the
            // next. Only the DB link write below needs to run strictly one at a time.
            var indexed = await WaitForOutputItemAsync(outputPath, cancellationToken).ConfigureAwait(false);
            if (indexed is null)
            {
                _logger.LogWarning(
                    "Auto-merge skipped: transcoded output {Path} did not appear as a library item within {Timeout}; it can still be merged manually",
                    outputPath,
                    FindTimeout);
                return;
            }

            await _mergeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_libraryManager.FindByPath(sourcePath, false) is not Video primary)
                {
                    _logger.LogWarning("Auto-merge skipped: source {Path} is not a known Jellyfin video item", sourcePath);
                    return;
                }

                // Re-resolve the output under the lock — it may have been re-indexed or removed between the
                // poll and acquiring the lock.
                if (_libraryManager.FindByPath(outputPath, false) is not Video alternate)
                {
                    return;
                }

                if (alternate.Id.Equals(primary.Id))
                {
                    // Jellyfin already treats them as one item (e.g. matching version filenames).
                    return;
                }

                // Re-fetch the canonical, cached instances by id. FindByPath materialises fresh detached
                // copies; mutating those races the copy the scanner holds. GetItemById returns the shared
                // instance, so the merge writes to the same object subsequent saves see.
                primary = _libraryManager.GetItemById(primary.Id) as Video ?? primary;
                alternate = _libraryManager.GetItemById(alternate.Id) as Video ?? alternate;

                MergeInto(primary, alternate);
                await alternate.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
                await primary.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);

                _logger.LogInformation("Registered {Output} as an alternate version of {Source}", outputPath, sourcePath);
            }
            finally
            {
                _mergeLock.Release();
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ObjectDisposedException)
        {
            // The plugin is shutting down and the merge lock was disposed under an in-flight detached
            // merge (these run on CancellationToken.None and can outlive a stop). Nothing to do.
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
        // Just wait for the file to be indexed; do NOT force a library scan. Jellyfin already rescans
        // when a file is added, and forcing an extra full scan per completed episode is precisely what
        // re-indexed the fresh file concurrently with the merge and clobbered the link (leaving a
        // duplicate episode) — besides being very expensive on a large library. If realtime monitoring is
        // off and the file is never indexed, the merge is simply skipped and can be done manually.
        for (var waited = TimeSpan.Zero; waited < FindTimeout; waited += PollInterval)
        {
            if (_libraryManager.FindByPath(outputPath, false) is Video found)
            {
                return found;
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }
}
