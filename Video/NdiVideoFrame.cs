using System.Runtime.InteropServices;

namespace Tractus.HtmlToNdi.Video;

internal sealed class NdiVideoFrame : IDisposable
{
    public NdiVideoFrame(int width, int height, int stride, IntPtr buffer)
    {
        Width = width;
        Height = height;
        Stride = stride;
        Buffer = buffer;
    }

    public int Width { get; }

    public int Height { get; }

    public int Stride { get; }

    public IntPtr Buffer { get; private set; }

    public DateTime Timestamp { get; set; }

    public static NdiVideoFrame CopyFrom(CapturedFrame frame)
    {
        var size = frame.SizeInBytes;
        var buffer = Marshal.AllocHGlobal(size);
        unsafe
        {
            System.Buffer.MemoryCopy((void*)frame.Buffer, (void*)buffer, size, size);
        }

        return new NdiVideoFrame(frame.Width, frame.Height, frame.Stride, buffer)
        {
            Timestamp = DateTime.UtcNow
        };
    }

    public void Dispose()
    {
        if (Buffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(Buffer);
            Buffer = IntPtr.Zero;
        }

        GC.SuppressFinalize(this);
    }
}
