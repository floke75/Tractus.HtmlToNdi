using System;

using Tractus.HtmlToNdi.Video;
using Xunit;

namespace Tractus.HtmlToNdi.Tests;

public class FrameTimeAveragerTests
{
    [Fact]
    public void ReportsAverageFrameRate()
    {
        var averager = new FrameTimeAverager(sampleCount: 4);
        var start = DateTime.UtcNow;

        averager.Observe(start);
        averager.Observe(start + TimeSpan.FromMilliseconds(16.683));
        averager.Observe(start + TimeSpan.FromMilliseconds(33.366));
        averager.Observe(start + TimeSpan.FromMilliseconds(50.049));

        var rate = averager.GetFrameRate(new FrameRate(60, 1));

        Assert.Equal(60000, rate.Numerator);
        Assert.Equal(1001, rate.Denominator);
    }

    [Fact]
    public void FallsBackWhenInsufficientSamples()
    {
        var averager = new FrameTimeAverager();
        var fallback = new FrameRate(50, 1);

        var rate = averager.GetFrameRate(fallback);

        Assert.Equal(fallback, rate);
    }
}
