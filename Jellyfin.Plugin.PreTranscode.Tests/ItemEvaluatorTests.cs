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
}
