using System;
using Tractus.HtmlToNdi.Video;
using Xunit;

namespace Tractus.HtmlToNdi.Tests;

public class FramePipelineTests
{
    [Fact]
    public void FrameRingBuffer_DropsOldestAndKeepsNewest()
    {
        var buffer = new FrameRingBuffer(2);
        var first = BufferedVideoFrame.Rent(16);
        first.Populate(2, 2, 8, DateTime.UtcNow);
        var second = BufferedVideoFrame.Rent(16);
        second.Populate(2, 2, 8, DateTime.UtcNow.AddMilliseconds(16));
        var third = BufferedVideoFrame.Rent(16);
        third.Populate(2, 2, 8, DateTime.UtcNow.AddMilliseconds(32));

        buffer.Enqueue(first);
        buffer.Enqueue(second);
        buffer.Enqueue(third);

        Assert.Equal(1, buffer.DroppedFrames);

        using var latest = buffer.TakeLatest();
        Assert.NotNull(latest);
        Assert.Equal(third.TimestampUtc, latest!.TimestampUtc);

        buffer.Clear();
    }

    [Fact]
    public void FrameTimeAverager_ComputesAverageFps()
    {
        var averager = new FrameTimeAverager(10);
        var start = DateTime.UtcNow;
        for (var i = 0; i < 6; i++)
        {
            averager.AddSample(start.AddMilliseconds(16.67 * i));
        }

        var fps = averager.GetAverageFps();
        Assert.InRange(fps, 59.0, 61.0);
    }

    [Theory]
    [InlineData("59.94", 60000, 1001)]
    [InlineData("60000/1001", 60000, 1001)]
    [InlineData("30000:1001", 30000, 1001)]
    [InlineData("50", 50, 1)]
    public void FrameRate_ParsesInputs(string input, int expectedN, int expectedD)
    {
        var rate = FrameRate.Parse(input);
        Assert.Equal(expectedN, rate.Numerator);
        Assert.Equal(expectedD, rate.Denominator);
    }
}
