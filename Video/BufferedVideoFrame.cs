using System;
using System.Buffers;

namespace Tractus.HtmlToNdi.Video;

public sealed class BufferedVideoFrame : IDisposable
{
    private readonly IMemoryOwner<byte> memoryOwner;
    private bool disposed;

    private BufferedVideoFrame(IMemoryOwner<byte> owner, int length)
    {
        memoryOwner = owner;
        Length = length;
    }

    public Memory<byte> Memory => memoryOwner.Memory.Slice(0, Length);

    public Span<byte> Span => memoryOwner.Memory.Span.Slice(0, Length);

    public int Length { get; }

    public int Width { get; private set; }

    public int Height { get; private set; }

    public int Stride { get; private set; }

    public DateTime TimestampUtc { get; private set; }

    public static BufferedVideoFrame Rent(int byteLength)
    {
        var owner = MemoryPool<byte>.Shared.Rent(byteLength);
        return new BufferedVideoFrame(owner, byteLength);
    }

    public void Populate(int width, int height, int stride, DateTime timestampUtc)
    {
        Width = width;
        Height = height;
        Stride = stride;
        TimestampUtc = timestampUtc;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        memoryOwner.Dispose();
    }
}
