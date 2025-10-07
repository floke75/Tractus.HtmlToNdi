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
    public void DropsOldestWhenCapacityReached()
    {
        var buffer = new FrameRingBuffer<DisposableStub>(2);
        var first = new DisposableStub();
        var second = new DisposableStub();
        var third = new DisposableStub();

        buffer.Enqueue(first, out var dropped1);
        Assert.Null(dropped1);

        buffer.Enqueue(second, out var dropped2);
        Assert.Null(dropped2);

        buffer.Enqueue(third, out var dropped3);
        Assert.Same(first, dropped3);
        dropped3?.Dispose();
        Assert.True(first.Disposed);
        Assert.Equal(1, buffer.DroppedFromOverflow);

        var latest = buffer.DequeueLatest();
        Assert.Same(third, latest);
        Assert.Equal(0, buffer.DroppedAsStale);
    }

    [Fact]
    public void DequeueLatestDropsStaleFrames()
    {
        var buffer = new FrameRingBuffer<DisposableStub>(3);
        var first = new DisposableStub();
        var second = new DisposableStub();
        var third = new DisposableStub();

        buffer.Enqueue(first, out _);
        buffer.Enqueue(second, out _);
        buffer.Enqueue(third, out _);

        var latest = buffer.DequeueLatest();
        Assert.Same(third, latest);
        Assert.True(first.Disposed);
        Assert.True(second.Disposed);
        Assert.Equal(2, buffer.DroppedAsStale);
    }

    [Fact]
    public void TryDequeueReturnsOldestWithoutDisposing()
    {
        var buffer = new FrameRingBuffer<DisposableStub>(3);
        var first = new DisposableStub();
        var second = new DisposableStub();

        buffer.Enqueue(first, out _);
        buffer.Enqueue(second, out _);

        var success = buffer.TryDequeue(out var dequeued);

        Assert.True(success);
        Assert.Same(first, dequeued);
        Assert.False(first.Disposed);
        Assert.Equal(1, buffer.Count);
        dequeued?.Dispose();
    }

    [Fact]
    public void TryDequeueMaintainsFifoAfterOverflow()
    {
        var buffer = new FrameRingBuffer<DisposableStub>(2);
        var first = new DisposableStub();
        var second = new DisposableStub();
        var third = new DisposableStub();

        buffer.Enqueue(first, out _);
        buffer.Enqueue(second, out _);
        buffer.Enqueue(third, out var dropped);

        Assert.Same(first, dropped);
        dropped?.Dispose();
        Assert.True(first.Disposed);
        Assert.Equal(1, buffer.DroppedFromOverflow);

        var success = buffer.TryDequeue(out var dequeued);

        Assert.True(success);
        Assert.Same(second, dequeued);
        Assert.False(second.Disposed);
        Assert.Equal(1, buffer.Count);
        dequeued?.Dispose();
    }
}
