using Tractus.HtmlToNdi.Video;
using Xunit;

namespace Tractus.HtmlToNdi.Tests;

public class FrameRateTests
{
    [Theory]
    [InlineData("59.94", 60000, 1001)]
    [InlineData("29.97", 30000, 1001)]
    [InlineData("50", 50, 1)]
    [InlineData("24", 24, 1)]
    [InlineData("60000/1001", 60000, 1001)]
    [InlineData("30000:1001", 30000, 1001)]
    public void ParseRecognisesBroadcastRates(string input, int expectedNumerator, int expectedDenominator)
    {
        var rate = FrameRate.Parse(input);

        Assert.Equal(expectedNumerator, rate.Numerator);
        Assert.Equal(expectedDenominator, rate.Denominator);
    }

    [Fact]
    public void FromDoubleProducesReasonableFraction()
    {
        var rate = FrameRate.FromDouble(47.952);

        Assert.True(Math.Abs(rate.Value - 47.952) < 0.0005);
        Assert.True(rate.Denominator <= 1000);
    }
}
