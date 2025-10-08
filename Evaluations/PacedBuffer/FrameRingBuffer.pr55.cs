using System.Diagnostics.CodeAnalysis;

namespace Tractus.HtmlToNdi.Video;

internal sealed class FrameRingBuffer<T>
    where T : class, IDisposable
{
    private readonly int capacity;
    private readonly Queue<T> frames;
    private int overflowSinceLastDequeue;

    public FrameRingBuffer(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        this.capacity = capacity;
        frames = new Queue<T>(capacity);
    }

    public int Capacity => capacity;

    public int Count
    {
        get
        {
            lock (frames)
            {
                return frames.Count;
            }
        }
    }

    public long DroppedFromOverflow { get; private set; }

    public long DroppedAsStale { get; private set; }

    public void Enqueue(T frame, out T? dropped)
    {
        dropped = null;
        lock (frames)
        {
            if (frames.Count == capacity)
            {
                dropped = frames.Dequeue();
                DroppedFromOverflow++;
                if (overflowSinceLastDequeue < capacity)
                {
                    overflowSinceLastDequeue++;
                }
            }

            frames.Enqueue(frame);
        }
    }

    public bool TryDequeue([NotNullWhen(true)] out T? frame)
    {
        lock (frames)
        {
            if (frames.Count == 0)
            {
                frame = null;
                return false;
            }

            frame = frames.Dequeue();
            if (overflowSinceLastDequeue > 0)
            {
                overflowSinceLastDequeue--;
            }

            return true;
        }
    }

    public T? DequeueLatest()
    {
        lock (frames)
        {
            if (frames.Count == 0)
            {
                return null;
            }

            while (frames.Count > 1)
            {
                var stale = frames.Dequeue();
                stale.Dispose();
                if (overflowSinceLastDequeue > 0)
                {
                    overflowSinceLastDequeue--;
                }
                else
                {
                    DroppedAsStale++;
                }
            }

            overflowSinceLastDequeue = 0;
            return frames.Dequeue();
        }
    }

    public void Clear()
    {
        lock (frames)
        {
            while (frames.Count > 0)
            {
                frames.Dequeue().Dispose();
            }
            overflowSinceLastDequeue = 0;
        }
    }

    public void DrainToLatestAndKeep()
    {
        lock (frames)
        {
            if (frames.Count <= 1)
            {
                return;
            }

            while (frames.Count > 1)
            {
                var stale = frames.Dequeue();
                stale.Dispose();
                if (overflowSinceLastDequeue > 0)
                {
                    overflowSinceLastDequeue--;
                }
                else
                {
                    DroppedAsStale++;
                }
            }
        }
    }
}
