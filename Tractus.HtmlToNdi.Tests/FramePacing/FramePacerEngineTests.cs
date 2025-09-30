using System;
using System.Collections.Generic;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Tractus.HtmlToNdi.FramePacing;
using Xunit;

namespace Tractus.HtmlToNdi.Tests.FramePacing;

public class FramePacerEngineTests
{
    [Fact]
    public void RingBufferReturnsLatestAndReportsDrops()
    {
        var buffer = new FrameRingBuffer<BrowserFrame>(3);
        long sequence = -1;

        for (var i = 0; i < 5; i++)
        {
            buffer.Push(CreateFrame(i + 1, 32, 18, DateTime.UtcNow.AddMilliseconds(i)));
        }

        Assert.Equal(3, buffer.GetBacklog(sequence));

        var hasFrame = buffer.TryGetLatest(ref sequence, out var latest, out var dropped);

        Assert.True(hasFrame);
        Assert.Equal(4, dropped);
        Assert.Equal(5, latest.PixelBuffer[0]);
        Assert.Equal(0, buffer.GetBacklog(sequence));
    }

    [Fact]
    public void EngineRepeatsAndDropsAsExpected()
    {
        var buffer = new FrameRingBuffer<BrowserFrame>(3);
        var logger = new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Sink(new NullSink()).CreateLogger();
        var sent = new List<(BrowserFrame frame, FrameDeliveryContext context, DateTime tick)>();
        var currentTick = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var engine = new FramePacerEngine(buffer, FrameRate.Default2997, (frame, context) =>
        {
            sent.Add((frame, context, currentTick));
        },
        logger);

        var interval = engine.Interval;

        var firstFrame = CreateFrame(1, 64, 36, currentTick);
        buffer.Push(firstFrame);

        engine.ProcessTick(currentTick);
        Assert.Single(sent);
        Assert.False(sent[0].context.IsRepeat);
        Assert.Equal(0, sent[0].context.DroppedFrames);

        currentTick = currentTick.Add(interval);
        engine.ProcessTick(currentTick);
        Assert.Equal(2, sent.Count);
        Assert.True(sent[1].context.IsRepeat);

        var secondFrameCapture = currentTick.AddMilliseconds(-5);
        var secondFrame = CreateFrame(2, 64, 36, secondFrameCapture);
        buffer.Push(secondFrame);

        currentTick = currentTick.Add(interval);
        engine.ProcessTick(currentTick);
        Assert.Equal(3, sent.Count);
        Assert.False(sent[2].context.IsRepeat);
        Assert.Equal(0, sent[2].context.DroppedFrames);

        buffer.Push(CreateFrame(3, 64, 36, currentTick.AddMilliseconds(-2)));
        buffer.Push(CreateFrame(4, 64, 36, currentTick.AddMilliseconds(-1)));

        currentTick = currentTick.Add(interval);
        engine.ProcessTick(currentTick);

        Assert.Equal(4, sent.Count);
        Assert.False(sent[3].context.IsRepeat);
        Assert.True(sent[3].context.DroppedFrames >= 1);
    }

    private static BrowserFrame CreateFrame(int id, int width, int height, DateTime capturedAt)
    {
        var stride = width * 4;
        var buffer = new byte[stride * height];
        buffer[0] = (byte)id;
        return new BrowserFrame(buffer, width, height, stride, (float)width / height, capturedAt);
    }

    private sealed class NullSink : ILogEventSink
    {
        public void Emit(LogEvent logEvent)
        {
        }
    }
}
