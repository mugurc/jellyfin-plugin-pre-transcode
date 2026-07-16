using System;
using System.IO;
using Jellyfin.Plugin.PreTranscode.Library;

namespace Jellyfin.Plugin.PreTranscode.Tests;

public class ItemEvaluatorTests
{
    [Theory]
    [InlineData("470f8009-e889-4134-af97-fa8158de2c04", "470f8009e8894134af97fa8158de2c04")]
    [InlineData("470f8009e8894134af97fa8158de2c04", "470f8009e8894134af97fa8158de2c04")]
    [InlineData("{470F8009-E889-4134-AF97-FA8158DE2C04}", "470f8009e8894134af97fa8158de2c04")]
    [InlineData("not-a-guid", "notaguid")]
    public void NormalizeGuid_NormalizesToDashlessLowercase(string input, string expected)
    {
        Assert.Equal(expected, ItemEvaluator.NormalizeGuid(input));
    }

    [Fact]
    public void IsStable_TrueWhenFileOlderThanWindow()
    {
        WithTempFile(DateTime.UtcNow.AddMinutes(-5), path => Assert.True(ItemEvaluator.IsStable(path, 60)));
    }

    [Fact]
    public void IsStable_FalseWhenFileRecentlyWritten()
    {
        // The just-added-file case: within the stability window, so the monitor defers instead of skipping.
        WithTempFile(DateTime.UtcNow, path => Assert.False(ItemEvaluator.IsStable(path, 60)));
    }

    [Fact]
    public void IsStable_TrueWhenWindowDisabled()
    {
        WithTempFile(DateTime.UtcNow, path => Assert.True(ItemEvaluator.IsStable(path, 0)));
    }

    private static void WithTempFile(DateTime lastWriteUtc, Action<string> assert)
    {
        var path = Path.GetTempFileName();
        try
        {
            File.SetLastWriteTimeUtc(path, lastWriteUtc);
            assert(path);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
