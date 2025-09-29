using System.Buffers;
using System.Runtime.InteropServices;

namespace Tractus.HtmlToNdi.Video;

/// <summary>
/// Represents a managed copy of a Chromium frame stored in pooled memory.
/// </summary>
public sealed class BufferedVideoFrame : IDisposable
{
    private static readonly ArrayPool<byte> Pool = ArrayPool<byte>.Shared;

    private int _disposed;
    private readonly int _bufferLength;

    private BufferedVideoFrame(byte[] buffer, int stride, int width, int height)
    {
        Buffer = buffer;
        Stride = stride;
        Width = width;
        Height = height;
        _bufferLength = stride * height;
        CapturedAt = DateTime.UtcNow;
    }

    public byte[] Buffer { get; }

    public int Stride { get; }

    public int Width { get; }

    public int Height { get; }

    public DateTime CapturedAt { get; }

    public static BufferedVideoFrame Rent(int width, int height, int stride)
    {
        var size = stride * height;
        var rented = Pool.Rent(size);
        return new BufferedVideoFrame(rented, stride, width, height);
    }

    public void CopyFrom(IntPtr source, int bytes)
    {
        if (_disposed != 0)
        {
            throw new ObjectDisposedException(nameof(BufferedVideoFrame));
        }

        if (bytes > _bufferLength)
        {
            throw new ArgumentOutOfRangeException(nameof(bytes));
        }

        Marshal.Copy(source, Buffer, 0, bytes);
    }

    public Span<byte> GetSpan() => Buffer.AsSpan(0, _bufferLength);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            Pool.Return(Buffer);
        }
    }
}
