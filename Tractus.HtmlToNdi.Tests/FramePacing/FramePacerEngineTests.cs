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
            buffer.Push(new BrowserFrame(new nint(i + 1), 1920, 1080, 1920 * 4, 16f / 9f, DateTime.UtcNow.AddMilliseconds(i)));
        }

        Assert.Equal(3, buffer.GetBacklog(sequence));

        var hasFrame = buffer.TryGetLatest(ref sequence, out var latest, out var dropped);

        Assert.True(hasFrame);
        Assert.Equal(4, dropped);
        Assert.Equal(5, latest.BufferHandle.ToInt64());
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

        var firstFrame = new BrowserFrame(new nint(1), 640, 360, 640 * 4, 16f / 9f, currentTick);
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
        var secondFrame = new BrowserFrame(new nint(2), 640, 360, 640 * 4, 16f / 9f, secondFrameCapture);
        buffer.Push(secondFrame);

        currentTick = currentTick.Add(interval);
        engine.ProcessTick(currentTick);
        Assert.Equal(3, sent.Count);
        Assert.False(sent[2].context.IsRepeat);
        Assert.Equal(0, sent[2].context.DroppedFrames);

        buffer.Push(new BrowserFrame(new nint(3), 640, 360, 640 * 4, 16f / 9f, currentTick.AddMilliseconds(-2)));
        buffer.Push(new BrowserFrame(new nint(4), 640, 360, 640 * 4, 16f / 9f, currentTick.AddMilliseconds(-1)));

        currentTick = currentTick.Add(interval);
        engine.ProcessTick(currentTick);

        Assert.Equal(4, sent.Count);
        Assert.False(sent[3].context.IsRepeat);
        Assert.True(sent[3].context.DroppedFrames >= 1);
    }

    private sealed class NullSink : ILogEventSink
    {
        public void Emit(LogEvent logEvent)
        {
        }
    }
}
