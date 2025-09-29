using System.Threading;

namespace Tractus.HtmlToNdi.FramePacing;

/// <summary>
/// Single-producer single-consumer ring buffer that overwrites the oldest frames when full.
/// </summary>
/// <typeparam name="T">Value type stored in the buffer.</typeparam>
public sealed class FrameRingBuffer<T>
    where T : struct
{
    private readonly T[] buffer;
    private long writeSequence = -1;
    private long publishedSequence = -1;

    public FrameRingBuffer(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be greater than zero.");
        }

        this.buffer = new T[capacity];
    }

    public int Capacity => this.buffer.Length;

    public long LatestSequence => Volatile.Read(ref this.publishedSequence);

    public void Push(T value)
    {
        var sequence = Interlocked.Increment(ref this.writeSequence);
        this.buffer[(int)(sequence % this.buffer.Length)] = value;
        Volatile.Write(ref this.publishedSequence, sequence);
    }

    public bool TryGetLatest(ref long lastSequence, out T value, out int dropped)
    {
        var latest = Volatile.Read(ref this.publishedSequence);
        if (latest < 0 || latest == lastSequence)
        {
            value = default;
            dropped = 0;
            return false;
        }

        dropped = (int)Math.Max(0, latest - lastSequence - 1);
        value = this.buffer[(int)(latest % this.buffer.Length)];
        lastSequence = latest;
        return true;
    }

    public int GetBacklog(long lastSequence)
    {
        var latest = Volatile.Read(ref this.publishedSequence);
        if (latest < 0)
        {
            return 0;
        }

        return (int)Math.Min(this.buffer.Length, Math.Max(0, latest - lastSequence));
    }
}
