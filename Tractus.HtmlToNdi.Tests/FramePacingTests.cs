using System;
using System.Linq;
using Tractus.HtmlToNdi.Chromium;

namespace Tractus.HtmlToNdi.Tests;

internal static class FrameTestHelper
{
    internal static FrameData CreateFrame(byte fillValue, int width = 2, int height = 2)
    {
        var stride = width * 4;
        var buffer = Enumerable.Repeat(fillValue, stride * height).Select(v => (byte)v).ToArray();
        return FrameData.Create(buffer, width, height, stride, DateTimeOffset.UtcNow);
    }
}

public class FrameRingBufferTests
{
    [Fact]
    public void ReadLatest_ReturnsLatestFrameAndDropCount()
    {
        using var buffer = new FrameRingBuffer(3);
        var first = FrameTestHelper.CreateFrame(1);
        var firstResult = buffer.ReadLatest(0);
        Assert.False(firstResult.HasFrame);

        var seq1 = buffer.Enqueue(first);
        var resultAfterFirst = buffer.ReadLatest(0);
        Assert.True(resultAfterFirst.HasFrame);
        Assert.Equal(seq1, resultAfterFirst.Sequence);
        Assert.Equal(0, resultAfterFirst.DroppedCount);

        buffer.Enqueue(FrameTestHelper.CreateFrame(2));
        buffer.Enqueue(FrameTestHelper.CreateFrame(3));
        var lastFrame = FrameTestHelper.CreateFrame(4);
        buffer.Enqueue(lastFrame);

        var latest = buffer.ReadLatest(resultAfterFirst.Sequence);
        Assert.True(latest.HasFrame);
        Assert.Equal(lastFrame, latest.Frame);
        Assert.Equal(2, latest.DroppedCount);
    }
}

public class FramePacerTests
{
    private static FramePacingOptions DefaultOptions => new FramePacingOptions(30, 3, 60);

    [Fact]
    public void TryGetFrameForDispatch_RepeatsWhenNoNewFrame()
    {
        using var buffer = new FrameRingBuffer(3);
        using var pacer = new FramePacer(buffer, DefaultOptions);

        buffer.Enqueue(FrameTestHelper.CreateFrame(1));
        var now = DateTimeOffset.UtcNow;
        var first = pacer.TryGetFrameForDispatch(now);
        Assert.True(first.HasValue);
        Assert.False(first.Value.IsRepeat);
        Assert.Equal(0, first.Value.DroppedFrames);

        var second = pacer.TryGetFrameForDispatch(now + DefaultOptions.TargetInterval);
        Assert.True(second.HasValue);
        Assert.True(second.Value.IsRepeat);
        Assert.Same(first.Value.Frame, second.Value.Frame);
    }

    [Fact]
    public void TryGetFrameForDispatch_DropsIntermediateFrames()
    {
        using var buffer = new FrameRingBuffer(3);
        using var pacer = new FramePacer(buffer, DefaultOptions);

        buffer.Enqueue(FrameTestHelper.CreateFrame(1));
        var now = DateTimeOffset.UtcNow;
        var first = pacer.TryGetFrameForDispatch(now);
        Assert.True(first.HasValue);
        Assert.False(first.Value.IsRepeat);

        buffer.Enqueue(FrameTestHelper.CreateFrame(2));
        buffer.Enqueue(FrameTestHelper.CreateFrame(3));
        var newest = FrameTestHelper.CreateFrame(4);
        buffer.Enqueue(newest);

        var second = pacer.TryGetFrameForDispatch(now + DefaultOptions.TargetInterval);
        Assert.True(second.HasValue);
        Assert.False(second.Value.IsRepeat);
        Assert.Equal(2, second.Value.DroppedFrames);
        Assert.Equal(newest, second.Value.Frame);
    }
}

public class FrameRateMathTests
{
    [Theory]
    [InlineData(29.97, 30000, 1001)]
    [InlineData(30, 30, 1)]
    [InlineData(59.94, 60000, 1001)]
    public void ToRational_ReturnsExpectedFraction(double fps, int expectedN, int expectedD)
    {
        var (n, d) = FrameRateMath.ToRational(fps);
        Assert.Equal(expectedN, n);
        Assert.Equal(expectedD, d);
    }
}
