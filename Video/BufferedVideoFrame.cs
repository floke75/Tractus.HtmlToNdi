using System.Buffers;
using System.Runtime.InteropServices;

namespace Tractus.HtmlToNdi.Video;

public sealed class BufferedVideoFrame : IDisposable
{
    private IMemoryOwner<byte>? _memoryOwner;

    public BufferedVideoFrame(IMemoryOwner<byte> memoryOwner, int width, int height, int stride, int dataLength)
    {
        _memoryOwner = memoryOwner ?? throw new ArgumentNullException(nameof(memoryOwner));
        Width = width;
        Height = height;
        Stride = stride;
        DataLength = dataLength;
    }

    public int Width { get; }

    public int Height { get; }

    public int Stride { get; }

    public int DataLength { get; }

    public Memory<byte> Memory
    {
        get
        {
            if (_memoryOwner is null)
            {
                throw new ObjectDisposedException(nameof(BufferedVideoFrame));
            }

            return _memoryOwner.Memory[..DataLength];
        }
    }

    public MemoryHandle Pin()
    {
        if (_memoryOwner is null)
        {
            throw new ObjectDisposedException(nameof(BufferedVideoFrame));
        }

        return _memoryOwner.Memory.Pin();
    }

    public void Dispose()
    {
        _memoryOwner?.Dispose();
        _memoryOwner = null;
    }
}
