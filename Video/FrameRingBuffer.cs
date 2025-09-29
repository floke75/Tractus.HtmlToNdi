using System.Threading;

namespace Tractus.HtmlToNdi.Video;

public sealed class FrameRingBuffer
{
    private readonly FrameSlot[] slots;
    private readonly int capacity;
    private long writeSequence = -1;
    private long publishedSequence = -1;
    private long lastReadSequence = -1;

    public FrameRingBuffer(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        this.capacity = capacity;
        this.slots = new FrameSlot[capacity];
        for (var i = 0; i < capacity; i++)
        {
            this.slots[i] = new FrameSlot();
        }
    }

    public int Capacity => this.capacity;

    public void WriteFrame(ReadOnlySpan<byte> pixels, int width, int height, int stride, DateTime capturedAtUtc)
    {
        var sequence = Interlocked.Increment(ref this.writeSequence);
        var slot = this.slots[(int)(sequence % this.capacity)];
        slot.Write(pixels, width, height, stride, capturedAtUtc);
        Volatile.Write(ref this.publishedSequence, sequence);
    }

    public bool TryCopyLatest(Span<byte> destination, out FrameMetadata metadata, out int droppedFrames)
    {
        metadata = default;
        droppedFrames = 0;

        var published = Volatile.Read(ref this.publishedSequence);
        if (published < 0)
        {
            return false;
        }

        var lastRead = Volatile.Read(ref this.lastReadSequence);
        if (published == lastRead)
        {
            return false;
        }

        var slot = this.slots[(int)(published % this.capacity)];
        slot.CopyTo(destination);
        metadata = slot.Metadata;
        droppedFrames = (int)Math.Max(0, published - lastRead - 1);
        Volatile.Write(ref this.lastReadSequence, published);
        return true;
    }

    public FrameBufferSnapshot GetLatestSnapshot()
    {
        var published = Volatile.Read(ref this.publishedSequence);
        if (published < 0)
        {
            return FrameBufferSnapshot.Empty;
        }

        var slot = this.slots[(int)(published % this.capacity)];
        return new FrameBufferSnapshot(slot.Metadata, slot.AsMemory());
    }

    private sealed class FrameSlot
    {
        private byte[] buffer = Array.Empty<byte>();
        private FrameMetadata metadata;

        public FrameMetadata Metadata => this.metadata;

        public void Write(ReadOnlySpan<byte> pixels, int width, int height, int stride, DateTime capturedAtUtc)
        {
            var required = pixels.Length;
            if (this.buffer.Length < required)
            {
                this.buffer = new byte[required];
            }

            pixels.CopyTo(this.buffer);
            this.metadata = new FrameMetadata(width, height, stride, required, capturedAtUtc);
        }

        public void CopyTo(Span<byte> destination)
        {
            if (destination.Length < this.metadata.BufferLength)
            {
                throw new ArgumentException("Destination buffer too small", nameof(destination));
            }

            this.buffer.AsSpan(0, this.metadata.BufferLength).CopyTo(destination);
        }

        public ReadOnlyMemory<byte> AsMemory() => new ReadOnlyMemory<byte>(this.buffer, 0, this.metadata.BufferLength);
    }
}

public readonly struct FrameBufferSnapshot
{
    public static readonly FrameBufferSnapshot Empty = new FrameBufferSnapshot(default, ReadOnlyMemory<byte>.Empty);

    public FrameBufferSnapshot(FrameMetadata metadata, ReadOnlyMemory<byte> buffer)
    {
        this.Metadata = metadata;
        this.Buffer = buffer;
    }

    public FrameMetadata Metadata { get; }
    public ReadOnlyMemory<byte> Buffer { get; }

    public bool HasValue => this.Buffer.Length > 0;
}
