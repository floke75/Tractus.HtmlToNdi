using System;
using System.Threading;

namespace Tractus.HtmlToNdi.Video;

/// <summary>
/// Single-producer single-consumer ring buffer for video frames.
/// </summary>
public sealed class FrameRingBuffer
{
    private readonly VideoFrameData?[] buffer;
    private readonly int capacity;
    private long writeSequence;
    private long publishedSequence;

    public FrameRingBuffer(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        this.capacity = capacity;
        this.buffer = new VideoFrameData?[capacity];
        this.writeSequence = 0;
        this.publishedSequence = 0;
    }

    public int Capacity => this.capacity;

    public long PublishedSequence => Volatile.Read(ref this.publishedSequence);

    public long Write(VideoFrameData frame)
    {
        var sequence = Interlocked.Increment(ref this.writeSequence);
        var slot = (int)((sequence - 1) % this.capacity);

        this.buffer[slot] = frame;
        Volatile.Write(ref this.publishedSequence, sequence);

        return sequence;
    }

    public bool TryGetLatest(ref long lastReadSequence, out VideoFrameData? frame, out long dropped)
    {
        var published = Volatile.Read(ref this.publishedSequence);
        if (published == lastReadSequence)
        {
            frame = null;
            dropped = 0;
            return false;
        }

        dropped = Math.Max(0, published - lastReadSequence - 1);
        frame = this.buffer[(int)((published - 1) % this.capacity)];
        lastReadSequence = published;
        return frame is not null;
    }
}
