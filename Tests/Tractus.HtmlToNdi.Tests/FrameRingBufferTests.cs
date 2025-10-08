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
    public void TryDequeueReturnsOldestWithoutDisposal()
    {
        var buffer = new FrameRingBuffer<DisposableStub>(3);
        var first = new DisposableStub();
        var second = new DisposableStub();

        buffer.Enqueue(first, out _);
        buffer.Enqueue(second, out _);

        var successFirst = buffer.TryDequeue(out var dequeuedFirst);
        Assert.True(successFirst);
        Assert.Same(first, dequeuedFirst);
        Assert.False(first.Disposed);

        var successSecond = buffer.TryDequeue(out var dequeuedSecond);
        Assert.True(successSecond);
        Assert.Same(second, dequeuedSecond);
        Assert.False(second.Disposed);
    }

    [Fact]
    public void TryDequeueReturnsFalseWhenEmpty()
    {
        var buffer = new FrameRingBuffer<DisposableStub>(2);

        var success = buffer.TryDequeue(out var dequeued);

        Assert.False(success);
        Assert.Null(dequeued);
    }

    [Fact]
    public void DiscardAllButLatestDropsAllOlderEntries()
    {
        var buffer = new FrameRingBuffer<DisposableStub>(4);
        var first = new DisposableStub();
        var second = new DisposableStub();
        var third = new DisposableStub();

        buffer.Enqueue(first, out _);
        buffer.Enqueue(second, out _);
        buffer.Enqueue(third, out _);

        var discarded = buffer.DiscardAllButLatest();

        Assert.Equal(2, discarded);
        Assert.True(first.Disposed);
        Assert.True(second.Disposed);
        Assert.False(third.Disposed);
        Assert.Equal(2, buffer.DroppedAsStale);
        Assert.Equal(1, buffer.Count);

        var latest = buffer.DequeueLatest();
        Assert.Same(third, latest);
    }
}
