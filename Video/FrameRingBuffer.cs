using System.Collections.Generic;
using System.Threading;

namespace Tractus.HtmlToNdi.Video;

public sealed class FrameRingBuffer
{
    private readonly Queue<BufferedVideoFrame> queue;
    private readonly object sync = new();
    private readonly int capacity;
    private long droppedFrames;

    public FrameRingBuffer(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        this.capacity = capacity;
        queue = new Queue<BufferedVideoFrame>(capacity);
    }

    public int Count
    {
        get
        {
            lock (sync)
            {
                return queue.Count;
            }
        }
    }

    public long DroppedFrames => Interlocked.Read(ref droppedFrames);

    public void Enqueue(BufferedVideoFrame frame)
    {
        BufferedVideoFrame? dropped = null;

        lock (sync)
        {
            if (queue.Count >= capacity)
            {
                dropped = queue.Dequeue();
                Interlocked.Increment(ref droppedFrames);
            }

            queue.Enqueue(frame);
        }

        dropped?.Dispose();
    }

    public BufferedVideoFrame? TakeLatest()
    {
        lock (sync)
        {
            if (queue.Count == 0)
            {
                return null;
            }

            var latest = queue.Dequeue();
            while (queue.Count > 0)
            {
                var stale = latest;
                latest = queue.Dequeue();
                stale.Dispose();
            }

            return latest;
        }
    }

    public void Clear()
    {
        lock (sync)
        {
            while (queue.Count > 0)
            {
                queue.Dequeue().Dispose();
            }
        }
    }
}
