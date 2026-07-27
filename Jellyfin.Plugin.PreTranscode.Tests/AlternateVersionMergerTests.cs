using System;
using System.Collections.Generic;
using Jellyfin.Plugin.PreTranscode.Library;

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
}
