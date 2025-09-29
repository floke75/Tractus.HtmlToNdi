using System;
using System.Collections.Generic;
using System.Linq;
using Tractus.HtmlToNdi.Video;
using Xunit;

namespace Tractus.HtmlToNdi.Tests;

internal static class FrameTestHelper
{
    internal static VideoFrame CreateFrame(byte fillValue, int width = 2, int height = 2)
    {
        var stride = width * 4;
        var buffer = Enumerable.Repeat(fillValue, stride * height).Select(v => (byte)v).ToArray();
        return VideoFrame.FromSpan(buffer, width, height, stride, DateTime.UtcNow);
    }
}

public class FrameRingBufferTests
{
    [Fact]
    public void ReadLatest_ReturnsMostRecentFrameAndDropCount()
    {
        using var buffer = new FrameRingBuffer(3);

        var firstRead = buffer.ReadLatest(0);
        Assert.False(firstRead.HasFrame);

        using var first = FrameTestHelper.CreateFrame(1);
        buffer.Enqueue(first);

        var afterFirst = buffer.ReadLatest(0);
        Assert.True(afterFirst.HasFrame);
        Assert.Equal(1, afterFirst.Sequence);
        Assert.Equal(0, afterFirst.DroppedCount);

        using var second = FrameTestHelper.CreateFrame(2);
        using var third = FrameTestHelper.CreateFrame(3);
        buffer.Enqueue(second);
        buffer.Enqueue(third);

        using var newest = FrameTestHelper.CreateFrame(4);
        buffer.Enqueue(newest);

        var latest = buffer.ReadLatest(afterFirst.Sequence);
        Assert.True(latest.HasFrame);
        Assert.Equal(4, latest.Sequence);
        Assert.Equal(2, latest.DroppedCount);
        Assert.Same(newest, latest.Frame);
    }
}

public class FramePacerTests
{
    [Fact]
    public void RunTick_RepeatsFrameWhenNoNewFrameArrives()
    {
        using var buffer = new FrameRingBuffer(3);
        var dispatched = new List<FrameDispatch>();
        using var pacer = new FramePacer(buffer, FrameRate.FromDouble(30), dispatched.Add, new FramePacerOptions { StartImmediately = false, MetricsLogInterval = TimeSpan.Zero });

        using var frame = FrameTestHelper.CreateFrame(1);
        buffer.Enqueue(frame);

        var now = DateTime.UtcNow;
        pacer.RunTick(now);
        pacer.RunTick(now + pacer.TargetInterval);

        Assert.Equal(2, dispatched.Count);
        Assert.False(dispatched[0].IsRepeat);
        Assert.True(dispatched[1].IsRepeat);
        Assert.Equal(frame, dispatched[0].Frame);
        Assert.Equal(frame, dispatched[1].Frame);
    }

    [Fact]
    public void RunTick_DropsIntermediateFrames()
    {
        using var buffer = new FrameRingBuffer(3);
        var dispatched = new List<FrameDispatch>();
        using var pacer = new FramePacer(buffer, FrameRate.FromDouble(30), dispatched.Add, new FramePacerOptions { StartImmediately = false, MetricsLogInterval = TimeSpan.Zero });

        using var first = FrameTestHelper.CreateFrame(1);
        buffer.Enqueue(first);

        var now = DateTime.UtcNow;
        pacer.RunTick(now);

        using var second = FrameTestHelper.CreateFrame(2);
        using var third = FrameTestHelper.CreateFrame(3);
        using var fourth = FrameTestHelper.CreateFrame(4);
        buffer.Enqueue(second);
        buffer.Enqueue(third);
        buffer.Enqueue(fourth);

        pacer.RunTick(now + pacer.TargetInterval);

        Assert.Equal(2, dispatched.Count);
        Assert.False(dispatched[1].IsRepeat);
        Assert.Equal(2, dispatched[1].DroppedFrames);
        Assert.Equal(fourth, dispatched[1].Frame);
    }
}