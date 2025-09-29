using System.Collections.Generic;
using Serilog;
using Tractus.HtmlToNdi.Video;
using Xunit;

namespace Tractus.HtmlToNdi.Tests;

public class FramePipelineTests
{
    [Fact]
    public void FrameRingBuffer_DropsOldestWhenCapacityExceeded()
    {
        var buffer = new FrameRingBuffer(2);
        using var f1 = BufferedVideoFrame.Rent(10, 10, 40);
        using var f2 = BufferedVideoFrame.Rent(10, 10, 40);
        using var f3 = BufferedVideoFrame.Rent(10, 10, 40);

        Assert.Null(buffer.Enqueue(f1));
        Assert.Null(buffer.Enqueue(f2));
        var dropped = buffer.Enqueue(f3);
        Assert.Same(f1, dropped);

        buffer.Clear();
    }

    [Fact]
    public void FrameRingBuffer_TakeLatestReturnsNewest()
    {
        var buffer = new FrameRingBuffer(3);
        using var f1 = BufferedVideoFrame.Rent(10, 10, 40);
        using var f2 = BufferedVideoFrame.Rent(10, 10, 40);
        using var f3 = BufferedVideoFrame.Rent(10, 10, 40);

        buffer.Enqueue(f1);
        buffer.Enqueue(f2);
        buffer.Enqueue(f3);

        Assert.True(buffer.TryTakeLatest(out var latest, out var discarded));
        Assert.Same(f3, latest);
        Assert.Equal(2, discarded);
        Assert.Equal(0, buffer.Count);

        latest.Dispose();
        buffer.Clear();
    }

    [Fact]
    public void FramePacer_RepeatsLastFrameWhenProviderEmpty()
    {
        var logger = new LoggerConfiguration().CreateLogger();
        var provider = new TestFrameProvider();
        var consumer = new TestConsumer();
        using var frame1 = BufferedVideoFrame.Rent(10, 10, 40);
        using var frame2 = BufferedVideoFrame.Rent(10, 10, 40);

        provider.Enqueue(frame1);
        provider.Enqueue(frame2);

        var pacer = new FramePacer(new FrameRate(60, 1), provider, consumer, logger);

        pacer.ProcessTick();
        pacer.ProcessTick();
        pacer.ProcessTick();
        pacer.Dispose();

        Assert.Collection(consumer.Decisions,
            decision => Assert.Equal(FramePacerDecision.Fresh, decision),
            decision => Assert.Equal(FramePacerDecision.Fresh, decision),
            decision => Assert.Equal(FramePacerDecision.RepeatLast, decision));
    }

    private sealed class TestFrameProvider : IFrameProvider
    {
        private readonly Queue<BufferedVideoFrame> _frames = new();

        public void Enqueue(BufferedVideoFrame frame) => _frames.Enqueue(frame);

        public bool TryTakeLatest(out BufferedVideoFrame frame, out int discarded)
        {
            if (_frames.Count == 0)
            {
                frame = null!;
                discarded = 0;
                return false;
            }

            discarded = 0;
            frame = _frames.Dequeue();
            return true;
        }
    }

    private sealed class TestConsumer : IFrameConsumer
    {
        public List<FramePacerDecision> Decisions { get; } = new();

        public void OnFrame(BufferedVideoFrame? frame, FramePacerDecision decision, int discarded)
        {
            Decisions.Add(decision);
        }
    }
}
