using System;
using Tractus.HtmlToNdi.Video;
using Xunit;

namespace Tractus.HtmlToNdi.Tests;

public class FrameRingBufferTests
{
    [Fact]
    public void TryCopyLatest_ReturnsLatestFrameAndDropCount()
    {
        var buffer = new FrameRingBuffer(3);
        var firstPixels = CreatePixels(1, 3, 10);
        var secondPixels = CreatePixels(1, 3, 20);
        var thirdPixels = CreatePixels(1, 3, 30);
        var destination = new byte[firstPixels.Length];

        buffer.WriteFrame(firstPixels, 1, 3, 4, DateTime.UtcNow);
        Assert.True(buffer.TryCopyLatest(destination, out var metadata, out var dropped));
        Assert.Equal(0, dropped);
        Assert.Equal(1, metadata.Width);
        Assert.Equal(3, metadata.Height);
        Assert.Equal(firstPixels, destination);

        buffer.WriteFrame(secondPixels, 1, 3, 4, DateTime.UtcNow);
        buffer.WriteFrame(thirdPixels, 1, 3, 4, DateTime.UtcNow);

        Assert.True(buffer.TryCopyLatest(destination, out metadata, out dropped));
        Assert.Equal(1, dropped);
        Assert.Equal(thirdPixels, destination);

        Assert.False(buffer.TryCopyLatest(destination, out metadata, out dropped));
    }

    [Fact]
    public void TryCopyLatest_ReturnsFalseWhenEmpty()
    {
        var buffer = new FrameRingBuffer(2);
        var destination = new byte[8];

        Assert.False(buffer.TryCopyLatest(destination, out var metadata, out var dropped));
        Assert.Equal(default, metadata);
        Assert.Equal(0, dropped);
    }

    private static byte[] CreatePixels(int width, int height, byte seed)
    {
        var length = width * height * 4;
        var pixels = new byte[length];
        for (var i = 0; i < length; i++)
        {
            pixels[i] = (byte)(seed + i);
        }

        return pixels;
    }
}
