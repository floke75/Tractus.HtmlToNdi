using System.Buffers;

namespace Tractus.HtmlToNdi.Chromium;

/// <summary>
/// Represents a CPU-backed copy of a Chromium paint buffer that can be safely reused by a pacing thread.
/// </summary>
public sealed class VideoFrame : IDisposable
{
    private IMemoryOwner<byte>? memoryOwner;

    public VideoFrame(IMemoryOwner<byte> owner, int width, int height, int stride)
    {
        this.memoryOwner = owner;
        this.Width = width;
        this.Height = height;
        this.Stride = stride;
        this.BufferSize = stride * height;
    }

    public int Width { get; }

    public int Height { get; }

    public int Stride { get; }

    public int BufferSize { get; }

    public Memory<byte> Memory => (this.memoryOwner?.Memory ?? Memory<byte>.Empty).Slice(0, this.BufferSize);

    public Span<byte> Span => (this.memoryOwner is null ? Span<byte>.Empty : this.memoryOwner.Memory.Span.Slice(0, this.BufferSize));

    public void Dispose()
    {
        this.memoryOwner?.Dispose();
        this.memoryOwner = null;
    }

    public static VideoFrame FromPaintBuffer(IntPtr bufferHandle, int width, int height, int stride)
    {
        var owner = MemoryPool<byte>.Shared.Rent(stride * height);
        var frame = new VideoFrame(owner, width, height, stride);

        unsafe
        {
            var destination = frame.Span;
            var source = new Span<byte>(bufferHandle.ToPointer(), frame.BufferSize);
            source.CopyTo(destination);
        }

        return frame;
    }
}
