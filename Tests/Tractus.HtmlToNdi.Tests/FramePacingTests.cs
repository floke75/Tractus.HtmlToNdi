using System;
using System.Runtime.InteropServices;
using Tractus.HtmlToNdi.Chromium;
using Xunit;

namespace Tractus.HtmlToNdi.Tests;

public class VideoFrameBufferTests
{
    private PooledVideoFrame CreateTestFrame()
    {
        // Create a dummy pointer and dimensions for the test frame
        var source = new byte[16];
        var handle = GCHandle.Alloc(source, GCHandleType.Pinned);
        var frame = PooledVideoFrame.Rent(handle.AddrOfPinnedObject(), source.Length, 2, 2, 8);
        handle.Free();
        return frame;
    }

    [Fact]
    public void Buffer_EnqueuesAndDequeuesLatest_AndDropsOldest()
    {
        // Arrange
        var buffer = new VideoFrameBuffer(3);
        long lastSequence = -1;

        // Act
        // Enqueue 4 frames into a buffer of capacity 3.
        using (var f1 = CreateTestFrame()) buffer.Enqueue(f1); // seq 0
        using (var f2 = CreateTestFrame()) buffer.Enqueue(f2); // seq 1
        using (var f3 = CreateTestFrame()) buffer.Enqueue(f3); // seq 2

        // This frame should cause the oldest (seq 0) to be dropped.
        using (var f4 = CreateTestFrame()) buffer.Enqueue(f4); // seq 3

        // Read the latest frame. It should be frame4.
        var result = buffer.ReadLatest(ref lastSequence);

        // Assert
        Assert.NotNull(result.Frame);
        Assert.Equal(3, result.Sequence);
        // We started with no sequence (-1), so we read up to sequence 3.
        // 3 - (-1) - 1 = 3 frames were published since we last read.
        Assert.Equal(3, result.DroppedCount);

        // Clean up the frame we received
        result.Frame.Dispose();
    }

    [Fact]
    public void ReadLatest_WhenEmpty_ReturnsEmptyResult()
    {
        // Arrange
        var buffer = new VideoFrameBuffer(3);
        long lastSequence = -1;

        // Act
        var result = buffer.ReadLatest(ref lastSequence);

        // Assert
        Assert.Null(result.Frame);
        Assert.Equal(-1, lastSequence); // lastSequence should not be modified
    }

    [Fact]
    public void ReadLatest_WhenNoNewFrames_ReturnsEmptyResult()
    {
        // Arrange
        var buffer = new VideoFrameBuffer(3);
        long lastSequence = -1;

        using(var frame = CreateTestFrame())
        {
            buffer.Enqueue(frame);
        }

        var firstResult = buffer.ReadLatest(ref lastSequence);
        Assert.NotNull(firstResult.Frame);
        Assert.Equal(0, firstResult.Sequence);
        firstResult.Frame.Dispose(); // Pacer would dispose its reference

        // Act
        var secondResult = buffer.ReadLatest(ref lastSequence);

        // Assert
        Assert.Null(secondResult.Frame);
        Assert.Equal(0, lastSequence);
    }

    [Fact]
    public void ReferenceCounting_PreventsUseAfterFree()
    {
        // Arrange
        var buffer = new VideoFrameBuffer(1); // Capacity of 1 to force overwrite
        long lastSequence = -1;

        using var frame1 = CreateTestFrame();
        buffer.Enqueue(frame1);

        // Act
        // Pacer reads the latest frame, getting a reference to frame1
        var result1 = buffer.ReadLatest(ref lastSequence);
        var pacerHeldFrame = result1.Frame;
        Assert.NotNull(pacerHeldFrame);

        // Producer creates a new frame, which overwrites frame1 in the buffer.
        // The buffer disposes its reference to frame1, but the pacer still holds one.
        using var frame2 = CreateTestFrame();
        buffer.Enqueue(frame2);

        // Assert
        // This would throw an ObjectDisposedException if ref counting was broken.
        // We can still access the buffer of the frame held by the pacer.
        var span = pacerHeldFrame.Buffer;
        Assert.False(span.IsEmpty);

        // Pacer releases its reference. Now the frame should be disposed.
        pacerHeldFrame.Dispose();

        // This should now throw because the object is fully disposed.
        Assert.Throws<ObjectDisposedException>(() => pacerHeldFrame.Buffer);

        // Clean up the second frame
        var result2 = buffer.ReadLatest(ref lastSequence);
        result2.Frame?.Dispose();
    }
}