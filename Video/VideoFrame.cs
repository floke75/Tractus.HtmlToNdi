using System;
using System.Buffers;
using System.Runtime.InteropServices;
using System.Threading;

namespace Tractus.HtmlToNdi.Video;

public sealed class VideoFrame : IDisposable
{
    private readonly IMemoryOwner<byte> memoryOwner;
    private bool disposed;
    private int referenceCount;

    private VideoFrame(IMemoryOwner<byte> memoryOwner, int length, int width, int height, int stride, DateTime capturedAtUtc)
    {
        this.memoryOwner = memoryOwner;
        this.Length = length;
        this.Width = width;
        this.Height = height;
        this.Stride = stride;
        this.CapturedAtUtc = capturedAtUtc;
        this.referenceCount = 1;
    }

    public int Width { get; }

    public int Height { get; }

    public int Stride { get; }

    public int Length { get; }

    public DateTime CapturedAtUtc { get; }

    public ReadOnlySpan<byte> GetSpan()
    {
        this.ThrowIfDisposed();
        return this.memoryOwner.Memory.Span[..this.Length];
    }

    public unsafe void WithPointer(Action<IntPtr> action)
    {
        this.ThrowIfDisposed();

        var span = this.memoryOwner.Memory.Span.Slice(0, this.Length);
        ref var reference = ref MemoryMarshal.GetReference(span);

        fixed (byte* pointer = &reference)
        {
            action((IntPtr)pointer);
        }
    }

    public void AddReference()
    {
        while (true)
        {
            var current = Volatile.Read(ref this.referenceCount);
            if (current == 0)
            {
                throw new ObjectDisposedException(nameof(VideoFrame));
            }

            if (Interlocked.CompareExchange(ref this.referenceCount, current + 1, current) == current)
            {
                return;
            }
        }
    }

    public static VideoFrame FromPointer(IntPtr sourcePointer, int width, int height, int stride, DateTime capturedAtUtc)
    {
        if (sourcePointer == IntPtr.Zero)
        {
            throw new ArgumentNullException(nameof(sourcePointer));
        }

        var length = stride * height;
        var source = new ReadOnlySpan<byte>((void*)sourcePointer, length);
        return FromSpan(source, width, height, stride, capturedAtUtc);
    }

    public static VideoFrame FromSpan(ReadOnlySpan<byte> source, int width, int height, int stride, DateTime capturedAtUtc)
    {
        if (stride <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(stride));
        }

        var expectedLength = stride * height;
        if (source.Length < expectedLength)
        {
            throw new ArgumentException("Source buffer is smaller than expected frame size.", nameof(source));
        }

        var owner = MemoryPool<byte>.Shared.Rent(expectedLength);
        source[..expectedLength].CopyTo(owner.Memory.Span.Slice(0, expectedLength));
        return new VideoFrame(owner, expectedLength, width, height, stride, capturedAtUtc);
    }

    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        var newCount = Interlocked.Decrement(ref this.referenceCount);
        if (newCount < 0)
        {
            throw new ObjectDisposedException(nameof(VideoFrame));
        }

        if (newCount == 0)
        {
            this.memoryOwner.Dispose();
            this.disposed = true;
        }
    }

    private void ThrowIfDisposed()
    {
        if (this.disposed)
        {
            throw new ObjectDisposedException(nameof(VideoFrame));
        }
    }
}
