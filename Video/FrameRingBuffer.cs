using System;
using System.Threading;

namespace Tractus.HtmlToNdi.Video;

public sealed class FrameRingBuffer : IDisposable
{
    private readonly VideoFrame?[] slots;
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
        this.slots = new VideoFrame?[capacity];
        this.writeSequence = 0;
        this.publishedSequence = 0;
    }

    public int Capacity => this.capacity;

    public long Enqueue(VideoFrame frame)
    {
        if (frame is null)
        {
            throw new ArgumentNullException(nameof(frame));
        }

        var sequence = Interlocked.Increment(ref this.writeSequence);
        var index = (int)((sequence - 1) % this.capacity);
        frame.AddReference();
        var previous = Interlocked.Exchange(ref this.slots[index], frame);
        previous?.Dispose();
        Volatile.Write(ref this.publishedSequence, sequence);
        return sequence;
    }

    public FrameReadResult ReadLatest(long lastSequence)
    {
        while (true)
        {
            var current = Volatile.Read(ref this.publishedSequence);
            if (current == 0 || current == lastSequence)
            {
                return FrameReadResult.Empty(lastSequence);
            }

            var index = (int)((current - 1) % this.capacity);
            var frame = Volatile.Read(ref this.slots[index]);
            if (frame is null)
            {
                continue;
            }

            try
            {
                frame.AddReference();
            }
            catch (ObjectDisposedException)
            {
                // The producer disposed this slot before we could retain it. Try again with the latest sequence.
                continue;
            }

            if (Volatile.Read(ref this.publishedSequence) != current)
            {
                // A newer frame was published after we read the sequence. Release this frame and retry for the freshest data.
                frame.Dispose();
                continue;
            }

            var dropped = lastSequence == 0 ? 0 : (int)Math.Max(0, current - lastSequence - 1);
            return FrameReadResult.WithFrame(frame, current, dropped);
        }
    }

    public void Dispose()
    {
        for (var i = 0; i < this.slots.Length; i++)
        {
            Interlocked.Exchange(ref this.slots[i], null)?.Dispose();
        }
    }

    public readonly struct FrameReadResult
    {
        private FrameReadResult(VideoFrame? frame, long sequence, int dropped)
        {
            this.Frame = frame;
            this.Sequence = sequence;
            this.DroppedCount = dropped;
        }

        public VideoFrame? Frame { get; }

        public long Sequence { get; }

        public int DroppedCount { get; }

        public bool HasFrame => this.Frame is not null;

        public static FrameReadResult Empty(long sequence) => new(null, sequence, 0);

        public static FrameReadResult WithFrame(VideoFrame frame, long sequence, int dropped) => new(frame, sequence, dropped);
    }
}