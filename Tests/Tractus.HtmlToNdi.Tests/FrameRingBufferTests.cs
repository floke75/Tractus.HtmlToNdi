using Tractus.HtmlToNdi.Video;
using Xunit;

namespace Tractus.HtmlToNdi.Tests;

public class FrameRingBufferTests
{
    private sealed class DisposableStub : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose()
        {
            Disposed = true;
        }
    }

    [Fact]
    public void TryDequeueReturnsOldestWithoutDisposal()
    {
        var buffer = new FrameRingBuffer<DisposableStub>(3);
        var first = new DisposableStub();
        var second = new DisposableStub();
        var third = new DisposableStub();

        buffer.Enqueue(first, out _);
        buffer.Enqueue(second, out _);
        buffer.Enqueue(third, out _);

        var dequeuedFirst = buffer.TryDequeue(out var oldest);
        Assert.True(dequeuedFirst);
        Assert.Same(first, oldest);
        Assert.False(first.Disposed);

        var dequeuedSecond = buffer.TryDequeue(out var next);
        Assert.True(dequeuedSecond);
        Assert.Same(second, next);
        Assert.False(second.Disposed);

        oldest?.Dispose();
        next?.Dispose();
    }

    [Fact]
    public void TryDequeueReturnsFalseWhenEmpty()
    {
        var buffer = new FrameRingBuffer<DisposableStub>(2);
        var success = buffer.TryDequeue(out var dequeued);
        Assert.False(success);
        Assert.Null(dequeued);
    }
}