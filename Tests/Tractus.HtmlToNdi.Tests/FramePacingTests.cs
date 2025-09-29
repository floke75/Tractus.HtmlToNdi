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
        var source = new byte[2 * 2 * 4];
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
        buffer.Enqueue(CreateTestFrame()); // seq 1
        buffer.Enqueue(CreateTestFrame()); // seq 2
        buffer.Enqueue(CreateTestFrame()); // seq 3
        buffer.Enqueue(CreateTestFrame()); // seq 4, should drop frame 1

        // Read the latest frame. It should be frame 4.
        var result = buffer.ReadLatest(ref lastSequence);

        // Assert
        Assert.NotNull(result.Frame);
        Assert.Equal(4, result.Sequence);
        Assert.Equal(3, result.DroppedCount); // 4 - 0 - 1 = 3 frames were published since we last read

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
        Assert.Equal(-1, result.Sequence);
    }

    [Fact]
    public void ReadLatest_WhenNoNewFrames_ReturnsEmptyResult()
    {
        // Arrange
        var buffer = new VideoFrameBuffer(3);
        long lastSequence = -1;

        var frame = CreateTestFrame();
        buffer.Enqueue(frame);

        var firstResult = buffer.ReadLatest(ref lastSequence);
        Assert.NotNull(firstResult.Frame);
        firstResult.Frame.Dispose();

        // Act
        var secondResult = buffer.ReadLatest(ref lastSequence);

        // Assert
        Assert.Null(secondResult.Frame);
    }
}