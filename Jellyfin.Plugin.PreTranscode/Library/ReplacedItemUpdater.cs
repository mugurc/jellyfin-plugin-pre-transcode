using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PreTranscode.Encoding;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PreTranscode.Library;

/// <summary>
/// What the Jellyfin library needs after a "replace in place" transcode has swapped the file on disk.
/// </summary>
internal enum ReplacedItemAction
{
    /// <summary>The replaced file was never a library item; there is nothing to update.</summary>
    Nothing,

    /// <summary>An item already points at the right file, but its recorded media info is now stale.</summary>
    Reprobe,

    /// <summary>The item still points at the path the replacement deleted; repoint it, then re-probe.</summary>
    RepointAndReprobe
}

/// <summary>
/// Keeps the Jellyfin database in step with a <see cref="Configuration.OutputHandlingMode.ReplaceInPlace"/>
/// transcode.
/// <para>
/// The output's extension comes from the profile's container, so replacing <c>Movie.mp4</c> with an
/// <c>mkv</c> profile writes <c>Movie.mkv</c> and deletes <c>Movie.mp4</c>. To Jellyfin that is a rename:
/// the item's <c>Path</c> column still names the deleted file, and every playback attempt fails with a
/// file-not-found error until a library scan happens to come round — with real-time monitoring off (the
/// default) that can be a day away. Repointing the row here closes that window.
/// </para>
/// <para>
/// Re-encoding also invalidates what the database holds <em>about</em> the file — codec, container,
/// bitrate, stream list — whether or not the name changed, so both cases end with a media re-probe.
/// </para>
/// <para>
/// Best-effort and never throws: the file is already safely in place, and a failure here only means
/// Jellyfin catches up at its next scan.
/// </para>
/// </summary>
internal sealed class ReplacedItemUpdater
{
    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<ReplacedItemUpdater> _logger;

    public ReplacedItemUpdater(
        ILibraryManager libraryManager,
        IProviderManager providerManager,
        IFileSystem fileSystem,
        ILogger<ReplacedItemUpdater> logger)
    {
        _libraryManager = libraryManager;
        _providerManager = providerManager;
        _fileSystem = fileSystem;
        _logger = logger;
    }

    /// <summary>
    /// Points the library item that described <paramref name="sourcePath"/> at
    /// <paramref name="finalPath"/> and queues a re-probe of its media info. Never throws.
    /// </summary>
    /// <param name="sourcePath">The path the transcode replaced.</param>
    /// <param name="finalPath">The path the replacement was written to.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task TryUpdateAsync(string sourcePath, string finalPath, CancellationToken cancellationToken)
    {
        try
        {
            var pathChanged = !OutputApplier.IsSameFile(sourcePath, finalPath);

            var replaced = _libraryManager.FindByPath(sourcePath, false) as Video;
            var atFinalPath = pathChanged ? _libraryManager.FindByPath(finalPath, false) as Video : replaced;

            // Only counts as "another item" when it really is a different row: with an unchanged path the
            // two lookups return the same item, and that is a re-probe, not a conflict.
            var otherItemAtFinalPath = atFinalPath is not null
                && (replaced is null || !atFinalPath.Id.Equals(replaced.Id));

            switch (Decide(replaced is not null, pathChanged, otherItemAtFinalPath))
            {
                case ReplacedItemAction.Nothing:
                    _logger.LogDebug(
                        "No library item points at {Path}; nothing to repoint after replacing it with {Output}",
                        sourcePath,
                        finalPath);
                    return;

                case ReplacedItemAction.Reprobe:
                {
                    // Either the name did not change (same container), or Jellyfin indexed the new file
                    // before this ran. In the latter case the stale row is left alone deliberately: two
                    // rows must never claim one path, and the scanner drops the one whose file is gone.
                    var target = otherItemAtFinalPath ? atFinalPath! : replaced!;
                    QueueReprobe(target);
                    return;
                }

                default:
                {
                    // Re-fetch the canonical, cached instance by id. FindByPath materialises a fresh
                    // detached copy, so saving that one would leave the instance the scanner holds still
                    // carrying the deleted path.
                    var item = _libraryManager.GetItemById(replaced!.Id) as Video ?? replaced;

                    item.Path = finalPath;
                    ClearStaleFileFacts(item, finalPath);

                    // DateModified is deliberately NOT refreshed to the new file's timestamp. What decides
                    // whether the file is re-read is BaseItem.RequiresRefresh(), which compares the
                    // recorded DateModified against the file's own write time and is captured by
                    // MetadataService before BeforeSave overwrites it; a stale value there is what makes
                    // runAllProviders true and sends the queued refresh below to ffprobe.
                    await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataImport, cancellationToken).ConfigureAwait(false);

                    _logger.LogInformation(
                        "Repointed library item {Name} from {Source} to {Output}", item.Name, sourcePath, finalPath);

                    QueueReprobe(item);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Could not update the library item for {Source} after replacing it with {Output}; Jellyfin will catch up at its next scan",
                sourcePath,
                finalPath);
        }
    }

    /// <summary>
    /// The decision itself, over plain values, so it is testable without constructing Jellyfin entities.
    /// </summary>
    /// <param name="sourceIsLibraryItem">Whether the replaced path is a known library item.</param>
    /// <param name="pathChanged">Whether the replacement landed on a different path than the source.</param>
    /// <param name="otherItemAtFinalPath">Whether a <em>different</em> item already claims the new path.</param>
    /// <returns>What to do.</returns>
    internal static ReplacedItemAction Decide(bool sourceIsLibraryItem, bool pathChanged, bool otherItemAtFinalPath)
    {
        // An item already sitting at the new path wins outright: repointing the old row on top of it
        // would leave two library entries claiming the same file.
        if (otherItemAtFinalPath)
        {
            return ReplacedItemAction.Reprobe;
        }

        if (!sourceIsLibraryItem)
        {
            return ReplacedItemAction.Nothing;
        }

        return pathChanged ? ReplacedItemAction.RepointAndReprobe : ReplacedItemAction.Reprobe;
    }

    /// <summary>
    /// Corrects, in the same save as the path, the two facts about the file that the queued re-probe
    /// will not put right on its own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Container</c> is cleared rather than guessed. Jellyfin stores it as ffprobe's own comma list
    /// ("mkv,webm"), which cannot be derived from an extension, but <c>BaseItem.GetVersionInfo</c> falls
    /// back to the file's extension whenever the stored value is empty — so clearing it is right
    /// immediately, where leaving it was wrong until the refresh landed. That window mattered: replacing
    /// Movie.mp4 (H.264) with Movie.mkv (HEVC) left the database saying "mp4/h264", and Jellyfin will
    /// green-light direct play to a client that cannot decode what is actually in the file. Before the
    /// repoint the same window produced an obvious file-not-found; a silent wrong-codec negotiation is
    /// worse, so it must not outlive the save.
    /// </para>
    /// <para>
    /// <c>Size</c> is written because nothing else ever will. On 10.11 the media probe only assigns it
    /// for BluRay and DVD folders, and a library scan keeps the existing row rather than the freshly
    /// resolved one — so after shrinking a 20 GB file to 4 GB, every client would keep being told 20 GB
    /// indefinitely.
    /// </para>
    /// </remarks>
    /// <param name="item">The item being repointed.</param>
    /// <param name="finalPath">The file it now points at.</param>
    private static void ClearStaleFileFacts(BaseItem item, string finalPath)
    {
        item.Container = null;

        try
        {
            item.Size = new FileInfo(finalPath).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable size is not worth failing the repoint over; the old value is no more wrong than
            // it already was.
            item.Size = null;
        }
    }

    // Default rather than FullRefresh on purpose: FullRefresh would also replace metadata wholesale.
    // Default still runs every provider here — the stale DateModified makes runAllProviders true — so the
    // ffprobe re-read happens, at the cost of one remote metadata lookup per replaced file on Jellyfin's
    // serial refresh queue. Images are validated rather than refreshed, so existing artwork is neither
    // re-downloaded nor lost.
    private void QueueReprobe(BaseItem item)
    {
        var options = new MetadataRefreshOptions(new DirectoryService(_fileSystem))
        {
            MetadataRefreshMode = MetadataRefreshMode.Default,
            ImageRefreshMode = MetadataRefreshMode.ValidationOnly,
            ReplaceAllMetadata = false,
            ReplaceAllImages = false,
            IsAutomated = true
        };

        _providerManager.QueueRefresh(item.Id, options, RefreshPriority.High);
    }
}
