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
    public void DrainToLatestAndKeepResetsOverflowCounter()
    {
        // Arrange: Create a scenario where the number of drained items is less
        // than the overflow count, leaving a non-zero overflowSinceLastDequeue.
        var buffer = new FrameRingBuffer<DisposableStub>(5);

        // 1. Enqueue 10 frames to build up overflow.
        for (var i = 0; i < 10; i++)
        {
            buffer.Enqueue(new DisposableStub(), out var dropped);
            dropped?.Dispose();
        }
        // At this point: DroppedFromOverflow = 5, and the internal overflowSinceLastDequeue = 5.

        // 2. Dequeue some frames, reducing overflowSinceLastDequeue but not to zero.
        for (var i = 0; i < 3; i++)
        {
            buffer.TryDequeue(out var dequeued);
            dequeued?.Dispose();
        }
        // At this point: 2 frames left in buffer. The internal overflowSinceLastDequeue = 5 - 3 = 2.

        // Act: Drain to the latest frame. This should consume one "overflow" credit
        // and then, with the fix, reset the counter to zero.
        buffer.DrainToLatestAndKeep();
        // At this point: 1 frame left. Loop ran once.
        // WITHOUT THE FIX: internal overflowSinceLastDequeue would be 1.
        // WITH THE FIX: internal overflowSinceLastDequeue is reset to 0.

        // 3. Fill the buffer again.
        for (var i = 0; i < 4; i++)
        {
            buffer.Enqueue(new DisposableStub(), out var dropped);
            dropped?.Dispose();
        }
        // At this point: Buffer is full with 5 new frames.

        // Assert: Dequeue the latest and check stale drops. If the counter was
        // not reset, the stale drop count will be off by one.
        var latest = buffer.DequeueLatest();
        latest?.Dispose();

        // DequeueLatest drops 4 frames.
        // With the fix (overflow counter is 0), all 4 should count as stale.
        // Without the fix (overflow counter is 1), only 3 would count as stale.
        Assert.Equal(4, buffer.DroppedAsStale);
    }
}
