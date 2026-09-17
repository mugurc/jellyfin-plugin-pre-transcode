using Jellyfin.Plugin.PreTranscode.Library;

namespace Jellyfin.Plugin.PreTranscode.Tests;

/// <summary>
/// The manual single-item search (issue #8). Jellyfin's own query can only match an item's name — for
/// an episode that is the episode title — so a show name, an "S03E06" or a release tag never found the
/// file. Matching happens here instead, over the label and the full path at once.
/// </summary>
public class ItemSearchTests
{
    private const string Label = "Some Show - S03E06 - The Episode Title";
    private const string Path = "/tv/Some Show/Season 03/Some.Show.S03E06.1080p.WEB-DL.mkv";

    [Fact]
    public void Matches_TokensFromDifferentFields()
    {
        // "some" and "s03e06" come from the label, "1080p" only exists in the file name.
        Assert.True(ItemSearch.Matches(Label, Path, new[] { "some", "s03e06", "1080p" }));
    }

    [Fact]
    public void Matches_TokenSpanningNoSingleField_IsFalse()
    {
        // "someshow" is in neither field: the label spaces it, the path dots it.
        Assert.False(ItemSearch.Matches(Label, Path, new[] { "someshow" }));
    }

    [Fact]
    public void Matches_FileNameOnlyFragment()
    {
        Assert.True(ItemSearch.Matches(Label, Path, new[] { "web-dl" }));
    }

    [Fact]
    public void Matches_IsCaseInsensitive()
    {
        Assert.True(ItemSearch.Matches(Label, Path, new[] { "SOME", "s03E06", "WEB-dl" }));
    }

    [Fact]
    public void Matches_EveryTokenMustMatch()
    {
        Assert.False(ItemSearch.Matches(Label, Path, new[] { "some", "s03e07" }));
        Assert.False(ItemSearch.Matches(Label, Path, new[] { "2160p" }));
    }

    [Fact]
    public void Matches_NoTokens_MatchesEverything()
    {
        // The endpoint rejects an empty query before this point; the helper itself stays total.
        Assert.True(ItemSearch.Matches(Label, Path, System.Array.Empty<string>()));
    }

    [Fact]
    public void Matches_MissingLabelOrPath_StillUsesTheOther()
    {
        Assert.True(ItemSearch.Matches(null, Path, new[] { "1080p" }));
        Assert.True(ItemSearch.Matches(Label, null, new[] { "episode" }));
        Assert.False(ItemSearch.Matches(null, null, new[] { "anything" }));
    }

    [Fact]
    public void Tokenize_SplitsOnWhitespaceRuns()
    {
        Assert.Equal(new[] { "some", "show", "s03e06" }, ItemSearch.Tokenize("  some   show\ts03e06\n"));
    }

    [Fact]
    public void Tokenize_BlankQuery_IsEmpty()
    {
        Assert.Empty(ItemSearch.Tokenize(null));
        Assert.Empty(ItemSearch.Tokenize(string.Empty));
        Assert.Empty(ItemSearch.Tokenize("   "));
    }
}
