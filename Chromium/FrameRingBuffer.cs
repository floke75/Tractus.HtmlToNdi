using System;
using System.Threading;

namespace Tractus.HtmlToNdi.Chromium;

internal sealed class FrameRingBuffer : IDisposable
{
    private readonly FrameData?[] slots;
    private readonly int capacity;
    private long nextSequence;
    private long publishedSequence;
    private readonly object writeLock = new();
    private bool disposed;

    public FrameRingBuffer(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        this.capacity = capacity;
        this.slots = new FrameData[capacity];
    }

    public int Capacity => this.capacity;

    public long Enqueue(FrameData frame)
    {
        if (frame is null)
        {
            throw new ArgumentNullException(nameof(frame));
        }

        lock (this.writeLock)
        {
            if (this.disposed)
            {
                throw new ObjectDisposedException(nameof(FrameRingBuffer));
            }

            var sequence = ++this.nextSequence;
            var index = (int)((sequence - 1) % this.capacity);

            var previous = this.slots[index];
            this.slots[index] = frame;
            previous?.Dispose();

            Volatile.Write(ref this.publishedSequence, sequence);
            return sequence;
        }
    }

    public FrameReadResult ReadLatest(long lastSequence)
    {
        var currentSequence = Volatile.Read(ref this.publishedSequence);
        if (currentSequence == 0 || currentSequence == lastSequence)
        {
            return FrameReadResult.Empty(lastSequence);
        }

        var index = (int)((currentSequence - 1) % this.capacity);
        var frame = this.slots[index];
        if (frame is null)
        {
            return FrameReadResult.Empty(lastSequence);
        }

        var dropped = lastSequence == 0 ? 0 : (int)Math.Max(0, currentSequence - lastSequence - 1);
        return FrameReadResult.WithFrame(frame, currentSequence, dropped);
    }

    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        lock (this.writeLock)
        {
            if (this.disposed)
            {
                return;
            }

            for (var i = 0; i < this.slots.Length; i++)
            {
                this.slots[i]?.Dispose();
                this.slots[i] = null;
            }

            this.disposed = true;
        }
    }

    internal readonly struct FrameReadResult
    {
        private FrameReadResult(FrameData? frame, long sequence, int dropped)
        {
            this.Frame = frame;
            this.Sequence = sequence;
            this.DroppedCount = dropped;
        }

        public FrameData? Frame { get; }

        public long Sequence { get; }

        public int DroppedCount { get; }

        public bool HasFrame => this.Frame is not null;

        public static FrameReadResult Empty(long sequence)
        {
            return new FrameReadResult(null, sequence, 0);
        }

        public static FrameReadResult WithFrame(FrameData frame, long sequence, int dropped)
        {
            return new FrameReadResult(frame, sequence, dropped);
        }
    }
}
