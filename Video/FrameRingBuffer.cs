namespace Tractus.HtmlToNdi.Video;

internal sealed class FrameRingBuffer<T>
    where T : class, IDisposable
{
    private readonly int capacity;
    private readonly Queue<T> frames;

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
            }

            frames.Enqueue(frame);
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
                DroppedAsStale++;
            }

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
        }
    }
}
