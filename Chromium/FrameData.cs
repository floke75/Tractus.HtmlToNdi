using System;
using System.Buffers;
using System.Runtime.InteropServices;

namespace Tractus.HtmlToNdi.Chromium;

internal sealed class FrameData : IDisposable
{
    private readonly IMemoryOwner<byte> memoryOwner;
    private bool disposed;

    private FrameData(IMemoryOwner<byte> memoryOwner, int length, int width, int height, int stride, DateTimeOffset capturedAt)
    {
        this.memoryOwner = memoryOwner;
        this.Length = length;
        this.Width = width;
        this.Height = height;
        this.Stride = stride;
        this.CapturedAt = capturedAt;
    }

    public int Width { get; }

    public int Height { get; }

    public int Stride { get; }

    public int Length { get; }

    public DateTimeOffset CapturedAt { get; }

    public ReadOnlySpan<byte> GetSpan()
    {
        return this.memoryOwner.Memory.Span[..this.Length];
    }

    public unsafe void WithPointer(Action<IntPtr> action)
    {
        if (this.disposed)
        {
            throw new ObjectDisposedException(nameof(FrameData));
        }

        var span = this.memoryOwner.Memory.Span.Slice(0, this.Length);
        ref var reference = ref MemoryMarshal.GetReference(span);

        fixed (byte* pointer = &reference)
        {
            action((IntPtr)pointer);
        }
    }

    public static FrameData Create(ReadOnlySpan<byte> source, int width, int height, int stride, DateTimeOffset capturedAt)
    {
        if (stride <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(stride));
        }

        var length = stride * height;
        if (source.Length < length)
        {
            throw new ArgumentException("Source buffer is smaller than expected frame size.", nameof(source));
        }

        var owner = MemoryPool<byte>.Shared.Rent(length);
        source[..length].CopyTo(owner.Memory.Span.Slice(0, length));
        return new FrameData(owner, length, width, height, stride, capturedAt);
    }

    public static unsafe FrameData Create(IntPtr sourcePointer, int width, int height, int stride, DateTimeOffset capturedAt)
    {
        if (sourcePointer == IntPtr.Zero)
        {
            throw new ArgumentNullException(nameof(sourcePointer));
        }

        var length = stride * height;
        var span = new ReadOnlySpan<byte>((void*)sourcePointer, length);
        return Create(span, width, height, stride, capturedAt);
    }

    public void Dispose()
    {
        if (!this.disposed)
        {
            this.memoryOwner.Dispose();
            this.disposed = true;
        }
    }
}
