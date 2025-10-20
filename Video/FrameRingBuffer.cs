using System.Diagnostics.CodeAnalysis;

namespace Tractus.HtmlToNdi.Video;

/// <summary>
/// A ring buffer for disposable frames.
/// </summary>
/// <typeparam name="T">The type of the frames.</typeparam>
internal sealed class FrameRingBuffer<T>
    where T : class, IDisposable
{
    private readonly int capacity;
    private readonly Queue<T> frames;
    private int overflowSinceLastDequeue;

    /// <summary>
    /// Initializes a new instance of the <see cref="FrameRingBuffer{T}"/> class.
    /// </summary>
    /// <param name="capacity">The capacity of the buffer.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if the capacity is not positive.</exception>
    public FrameRingBuffer(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        this.capacity = capacity;
        frames = new Queue<T>(capacity);
    }

    /// <summary>
    /// Gets the capacity of the buffer.
    /// </summary>
    public int Capacity => capacity;

    /// <summary>
    /// Gets the number of frames currently in the buffer.
    /// </summary>
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

    /// <summary>
    /// Gets the number of frames dropped due to overflow.
    /// </summary>
    public long DroppedFromOverflow { get; private set; }

    /// <summary>
    /// Gets the number of frames dropped as stale.
    /// </summary>
    public long DroppedAsStale { get; private set; }

    /// <summary>
    /// Enqueues a frame, dropping the oldest frame if the buffer is full.
    /// </summary>
    /// <param name="frame">The frame to enqueue.</param>
    /// <param name="dropped">The frame that was dropped, if any.</param>
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

    /// <summary>
    /// Attempts to dequeue a frame.
    /// </summary>
    /// <param name="frame">The dequeued frame, if any.</param>
    /// <returns>True if a frame was dequeued; otherwise, false.</returns>
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

    /// <summary>
    /// Attempts to dequeue a frame, marking it as stale if it was not an overflow.
    /// </summary>
    /// <param name="frame">The dequeued frame, if any.</param>
    /// <returns>True if a frame was dequeued; otherwise, false.</returns>
    public bool TryDequeueAsStale([NotNullWhen(true)] out T? frame)
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
            else
            {
                DroppedAsStale++;
            }

            return true;
        }
    }

    /// <summary>
    /// Dequeues the latest frame, disposing of any older frames.
    /// </summary>
    /// <returns>The latest frame, or null if the buffer is empty.</returns>
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

    /// <summary>
    /// Trims the buffer to a single, latest frame.
    /// </summary>
    public void TrimToSingleLatest()
    {
        lock (frames)
        {
            if (frames.Count <= 1)
            {
                overflowSinceLastDequeue = 0;
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

            overflowSinceLastDequeue = 0;
        }
    }

    /// <summary>
    /// Clears the buffer, disposing of all frames.
    /// </summary>
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
}
