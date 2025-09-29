using System;
using System.Collections.Generic;
using System.Linq;
using Tractus.HtmlToNdi.Video;
using Xunit;

namespace Tractus.HtmlToNdi.Tests;

public class FramePacerTests
{
    [Fact]
    public void RunTick_RepeatsLastFrameWhenNoNewData()
    {
        var buffer = new FrameRingBuffer(4);
        var outputs = new List<FrameOutput>();
        var options = new FramePacerOptions
        {
            StartImmediately = false,
            MetricsLogInterval = TimeSpan.FromHours(1)
        };

        using var pacer = new FramePacer(buffer, FrameRate.Ntsc2997, outputs.Add, options);
        var now = DateTime.UtcNow;

        pacer.RunTick(now);
        Assert.Empty(outputs);

        var pixels = CreatePixels(1, 2, 0x10);
        buffer.WriteFrame(pixels, 1, 2, 4, now);

        var firstTick = now + pacer.TargetInterval;
        pacer.RunTick(firstTick);

        Assert.Single(outputs);
        Assert.False(outputs[0].IsRepeat);
        Assert.Equal(1, outputs[0].Metadata.Width);
        Assert.Equal(2, outputs[0].Metadata.Height);
        Assert.Equal(pixels, outputs[0].PixelData.ToArray());

        var secondTick = firstTick + pacer.TargetInterval;
        pacer.RunTick(secondTick);

        Assert.Equal(2, outputs.Count);
        Assert.True(outputs[1].IsRepeat);
        Assert.Equal(outputs[0].PixelData.ToArray(), outputs[1].PixelData.ToArray());

        var burstFrameA = CreatePixels(1, 2, 0x20);
        var burstFrameB = CreatePixels(1, 2, 0x30);
        buffer.WriteFrame(burstFrameA, 1, 2, 4, secondTick + TimeSpan.FromMilliseconds(5));
        buffer.WriteFrame(burstFrameB, 1, 2, 4, secondTick + TimeSpan.FromMilliseconds(10));

        var thirdTick = secondTick + pacer.TargetInterval;
        pacer.RunTick(thirdTick);

        Assert.Equal(3, outputs.Count);
        var latest = outputs.Last();
        Assert.False(latest.IsRepeat);
        Assert.Equal(1, latest.DroppedFrames);
        Assert.Equal(burstFrameB, latest.PixelData.ToArray());
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
