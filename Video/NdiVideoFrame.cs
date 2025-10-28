using System.Runtime.InteropServices;

namespace Tractus.HtmlToNdi.Video;

/// <summary>
/// Represents a video frame to be sent over NDI.
/// </summary>
internal sealed class NdiVideoFrame : IDisposable
{
    /// <summary>
    /// Initializes a new instance of the <see cref="NdiVideoFrame"/> class.
    /// </summary>
    /// <param name="width">The width of the frame.</param>
    /// <param name="height">The height of the frame.</param>
    /// <param name="stride">The stride of the frame.</param>
    /// <param name="buffer">A pointer to the frame buffer.</param>
    public NdiVideoFrame(int width, int height, int stride, IntPtr buffer)
    {
        Width = width;
        Height = height;
        Stride = stride;
        Buffer = buffer;
    }

    /// <summary>
    /// Gets the width of the frame.
    /// </summary>
    public int Width { get; }

    /// <summary>
    /// Gets the height of the frame.
    /// </summary>
    public int Height { get; }

    /// <summary>
    /// Gets the stride of the frame.
    /// </summary>
    public int Stride { get; }

    /// <summary>
    /// Gets a pointer to the frame buffer.
    /// </summary>
    public IntPtr Buffer { get; private set; }

    /// <summary>
    /// Gets or sets the timestamp of the frame.
    /// </summary>
    public DateTime Timestamp { get; set; }

    /// <summary>
    /// Gets or sets the high-resolution capture timestamp expressed in <see cref="System.Diagnostics.Stopwatch"/> ticks.
    /// </summary>
    public long MonotonicTimestamp { get; set; }

    /// <summary>
    /// Creates a new <see cref="NdiVideoFrame"/> by copying data from a <see cref="CapturedFrame"/>.
    /// </summary>
    /// <param name="frame">The captured frame to copy from.</param>
    /// <returns>A new <see cref="NdiVideoFrame"/> instance.</returns>
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
            Timestamp = frame.TimestampUtc,
            MonotonicTimestamp = frame.MonotonicTimestamp,
        };
    }

    /// <summary>
    /// Releases the unmanaged resources used by the video frame.
    /// </summary>
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
