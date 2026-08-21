using Jellyfin.Plugin.PreTranscode.Library;

namespace Jellyfin.Plugin.PreTranscode.Tests;

/// <summary>
/// What "replace in place" owes the Jellyfin database once the file on disk has been swapped.
/// <para>
/// The output's extension comes from the profile's container, so an mkv profile turns Movie.mp4 into
/// Movie.mkv and deletes the original. Jellyfin keys an item on its path, so the row is left naming a
/// file that no longer exists and playback fails with "file not found" — reported as issue #5.
/// </para>
/// </summary>
public class ReplacedItemUpdaterTests
{
    // The reported bug: same folder, new extension, database still on the old name.
    [Fact]
    public void ContainerChanged_RepointsTheItem()
    {
        Assert.Equal(
            ReplacedItemAction.RepointAndReprobe,
            ReplacedItemUpdater.Decide(sourceIsLibraryItem: true, pathChanged: true, otherItemAtFinalPath: false));
    }

    // Same container: the name never moved, but the file behind it is a different encode now, so what the
    // database records about it — codec, bitrate, streams — is stale and has to be read again.
    [Fact]
    public void SamePath_StillReprobes()
    {
        Assert.Equal(
            ReplacedItemAction.Reprobe,
            ReplacedItemUpdater.Decide(sourceIsLibraryItem: true, pathChanged: false, otherItemAtFinalPath: false));
    }

    // A library scan can index the new file before this runs. Repointing the old row on top of that would
    // leave two items claiming one path; the scanner drops the one whose file is gone by itself.
    [Fact]
    public void NewPathAlreadyIndexed_LeavesTheStaleRowAlone()
    {
        Assert.Equal(
            ReplacedItemAction.Reprobe,
            ReplacedItemUpdater.Decide(sourceIsLibraryItem: true, pathChanged: true, otherItemAtFinalPath: true));
    }

    // A manually-queued file can sit outside any library, and a re-run finds the source already gone from
    // the database. Neither is an error.
    [Fact]
    public void SourceWasNeverALibraryItem_DoesNothing()
    {
        Assert.Equal(
            ReplacedItemAction.Nothing,
            ReplacedItemUpdater.Decide(sourceIsLibraryItem: false, pathChanged: true, otherItemAtFinalPath: false));
        Assert.Equal(
            ReplacedItemAction.Nothing,
            ReplacedItemUpdater.Decide(sourceIsLibraryItem: false, pathChanged: false, otherItemAtFinalPath: false));
    }

    // ...unless the output itself has already been indexed, which is the item worth re-probing.
    [Fact]
    public void SourceGoneButOutputIndexed_ReprobesTheOutput()
    {
        Assert.Equal(
            ReplacedItemAction.Reprobe,
            ReplacedItemUpdater.Decide(sourceIsLibraryItem: false, pathChanged: true, otherItemAtFinalPath: true));
    }
}
