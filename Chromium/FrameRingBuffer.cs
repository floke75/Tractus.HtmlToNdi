namespace Tractus.HtmlToNdi.Chromium;

/// <summary>
/// Simple drop-oldest ring buffer used to decouple Chromium paint cadence from the paced sender.
/// </summary>
public sealed class FrameRingBuffer : IDisposable
{
    private readonly Queue<VideoFrame> queue;
    private readonly object gate = new();
    private bool disposed;

    public FrameRingBuffer(int capacity)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        this.queue = new Queue<VideoFrame>(capacity);
        this.Capacity = capacity;
    }

    public int Capacity { get; }

    public int Count
    {
        get
        {
            lock (this.gate)
            {
                return this.queue.Count;
            }
        }
    }

    public long DroppedFrames { get; private set; }

    public void Enqueue(VideoFrame frame)
    {
        if (frame is null)
        {
            throw new ArgumentNullException(nameof(frame));
        }

        lock (this.gate)
        {
            this.ThrowIfDisposed();

            this.queue.Enqueue(frame);
            while (this.queue.Count > this.Capacity)
            {
                var dropped = this.queue.Dequeue();
                dropped.Dispose();
                this.DroppedFrames++;
            }
        }
    }

    public bool TryDequeue(out VideoFrame? frame)
    {
        lock (this.gate)
        {
            if (this.queue.Count > 0)
            {
                frame = this.queue.Dequeue();
                return true;
            }
        }

        frame = null;
        return false;
    }

    public void Clear()
    {
        lock (this.gate)
        {
            while (this.queue.Count > 0)
            {
                this.queue.Dequeue().Dispose();
            }
        }
    }

    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.Clear();
        this.disposed = true;
    }

    private void ThrowIfDisposed()
    {
        if (this.disposed)
        {
            throw new ObjectDisposedException(nameof(FrameRingBuffer));
        }
    }
}
