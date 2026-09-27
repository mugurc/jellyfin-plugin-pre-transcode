using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.PreTranscode.Library;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.PreTranscode.Tests;

/// <summary>
/// Whether the database-level merge is needed at all.
/// <para>
/// For a MOVIE the output is written as "&lt;movie folder&gt; - &lt;label&gt;.&lt;ext&gt;", which is
/// Jellyfin's own multi-version naming convention, so the scanner already groups the two files as
/// <em>local</em> alternate versions. Adding the database link on top makes Jellyfin count the same file
/// twice: <c>Video.GetAllItemsForMediaSources</c> on 10.11 concatenates <c>GetLinkedAlternateVersions()</c>
/// and <c>GetLocalAlternateVersionIds()</c> without de-duplicating (the <c>DistinctBy</c> exists only on
/// later builds), so the version picker lists the transcode twice — reproduced on a live 10.11.11 server.
/// The link is still needed for TV episodes, where the naming convention does nothing.
/// </para>
/// </summary>
public class AlternateVersionMergerTests
{
    private static readonly Guid Alt = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Other = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void AlreadyGroupedById_NeedsNoLink()
    {
        Assert.True(AlternateVersionMerger.IsAlreadyLocalVersion(
            new[] { Other, Alt }, Array.Empty<string>(), Alt, "/m/Movie - PT.mkv"));
    }

    // A freshly-indexed item resolves its local-version ids lazily, so the path list is the fallback.
    [Fact]
    public void AlreadyGroupedByPath_NeedsNoLink()
    {
        Assert.True(AlternateVersionMerger.IsAlreadyLocalVersion(
            Array.Empty<Guid>(), new[] { "/m/Movie - PT.mkv" }, Alt, "/m/Movie - PT.mkv"));
    }

    [Fact]
    public void PathComparisonIgnoresCase()
    {
        Assert.True(AlternateVersionMerger.IsAlreadyLocalVersion(
            null, new[] { "/m/MOVIE - PT.MKV" }, Alt, "/m/Movie - PT.mkv"));
    }

    // The episode case: no local grouping, so the database link is what makes the two one item.
    [Fact]
    public void NotGrouped_StillNeedsTheLink()
    {
        Assert.False(AlternateVersionMerger.IsAlreadyLocalVersion(
            new[] { Other }, new[] { "/m/Something Else.mkv" }, Alt, "/m/Movie - PT.mkv"));
    }

    [Fact]
    public void NothingKnown_StillNeedsTheLink()
    {
        Assert.False(AlternateVersionMerger.IsAlreadyLocalVersion(null, null, Alt, "/m/Movie - PT.mkv"));
        Assert.False(AlternateVersionMerger.IsAlreadyLocalVersion(
            Array.Empty<Guid>(), Array.Empty<string>(), Alt, "/m/Movie - PT.mkv"));
    }

    [Fact]
    public void MissingAlternatePath_DoesNotMatchByPath()
    {
        Assert.False(AlternateVersionMerger.IsAlreadyLocalVersion(
            null, new List<string> { "/m/Movie - PT.mkv" }, Alt, null));
    }
    // These exercise MergeInto against real Jellyfin entities rather than plain values, which is the
    // point: Jellyfin 12 moved this API (SetPrimaryVersionId now takes a Guid?, LinkedChild.Path gave way
    // to ItemId, and LinkedChild.Create is the intended factory). A signature that did not line up would
    // fail here at run time, not only at compile time — and the live-server route cannot reach this code,
    // because the scanner groups the companion file by name before the merge is ever attempted.
    private static Video VideoWithId(Guid id) => new Video { Id = id };

    [Fact]
    public void MergeInto_PinsTheSourceAsPrimaryAndLinksTheOutput()
    {
        var primary = VideoWithId(Alt);
        var alternate = VideoWithId(Other);

        AlternateVersionMerger.MergeInto(primary, alternate);

        // The source stays the primary version; the transcode points at it.
        Assert.Equal(Alt, alternate.PrimaryVersionId);

        var links = primary.LinkedAlternateVersions;
        Assert.Single(links);
        Assert.Equal(Other, links[0].ItemId);

        // The output must not keep a list of its own, or Jellyfin walks a chain of versions.
        Assert.Empty(alternate.LinkedAlternateVersions);
    }

    [Fact]
    public void MergeInto_RunTwice_DoesNotDuplicateTheLink()
    {
        var primary = VideoWithId(Alt);
        var alternate = VideoWithId(Other);

        AlternateVersionMerger.MergeInto(primary, alternate);
        AlternateVersionMerger.MergeInto(primary, alternate);

        // A re-queued job merging the same pair again used to be a real occurrence.
        Assert.Single(primary.LinkedAlternateVersions);
    }

    [Fact]
    public void MergeInto_AbsorbsLinksTheOutputCarried()
    {
        var third = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var primary = VideoWithId(Alt);
        var alternate = VideoWithId(Other);
        alternate.LinkedAlternateVersions = new[] { new LinkedChild { ItemId = third } };

        AlternateVersionMerger.MergeInto(primary, alternate);

        var ids = primary.LinkedAlternateVersions.Select(l => l.ItemId).ToList();
        Assert.Contains(Other, ids);
        Assert.Contains(third, ids);
        Assert.Empty(alternate.LinkedAlternateVersions);
    }

    [Fact]
    public void MergeInto_KeepsEveryIdlessLegacyLink()
    {
        // Links written by an older server carry a path and no id. Comparing them by id would make every
        // one of them look like a duplicate of the others (null == null), silently dropping versions a
        // user had merged by hand. They are carried over untouched instead.
        var primary = VideoWithId(Alt);
        primary.LinkedAlternateVersions = Array.Empty<LinkedChild>();
        var alternate = VideoWithId(Other);
        alternate.LinkedAlternateVersions = new[] { new LinkedChild(), new LinkedChild() };

        AlternateVersionMerger.MergeInto(primary, alternate);

        // The output's own link plus both id-less ones.
        Assert.Equal(3, primary.LinkedAlternateVersions.Length);
        Assert.Equal(2, primary.LinkedAlternateVersions.Count(l => l.ItemId is null));
    }

    [Fact]
    public void MergeInto_LeavesAnExistingLinkToTheSameOutputAlone()
    {
        var primary = VideoWithId(Alt);
        primary.LinkedAlternateVersions = new[] { new LinkedChild { ItemId = Other } };
        var alternate = VideoWithId(Other);

        AlternateVersionMerger.MergeInto(primary, alternate);

        Assert.Single(primary.LinkedAlternateVersions);
        Assert.Equal(Other, primary.LinkedAlternateVersions[0].ItemId);
    }

}
