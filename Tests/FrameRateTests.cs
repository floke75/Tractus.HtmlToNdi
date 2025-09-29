using Xunit;

using Tractus.HtmlToNdi.Video;

namespace Tractus.HtmlToNdi.Tests;

public class FrameRateTests
{
    [Theory]
    [InlineData("59.94", 60000, 1001)]
    [InlineData("29.97", 30000, 1001)]
    [InlineData("30000/1001", 30000, 1001)]
    [InlineData("24", 24, 1)]
    public void ParsesFractionalBroadcastRates(string input, int expectedNumerator, int expectedDenominator)
    {
        var fallback = new FrameRate(60, 1);

        var parsed = FrameRate.Parse(input, fallback);

        Assert.Equal(expectedNumerator, parsed.Numerator);
        Assert.Equal(expectedDenominator, parsed.Denominator);
    }

    [Fact]
    public void FallsBackWhenInputInvalid()
    {
        var fallback = new FrameRate(50, 1);

        var parsed = FrameRate.Parse("bogus", fallback);

        Assert.Equal(fallback, parsed);
    }
}
