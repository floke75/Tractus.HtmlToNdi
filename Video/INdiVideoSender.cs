using NewTek;
using NewTek.NDI;

namespace Tractus.HtmlToNdi.Video;

/// <summary>
/// Defines a contract for sending NDI video frames.
/// </summary>
internal interface INdiVideoSender
{
    /// <summary>
    /// Sends an NDI video frame.
    /// </summary>
    /// <param name="frame">The video frame to send.</param>
    void Send(ref NDIlib.video_frame_v2_t frame);
}

/// <summary>
/// An implementation of <see cref="INdiVideoSender"/> that uses the native NDI library.
/// </summary>
internal sealed class NativeNdiVideoSender : INdiVideoSender
{
    private readonly nint senderPtr;

    /// <summary>
    /// Initializes a new instance of the <see cref="NativeNdiVideoSender"/> class.
    /// </summary>
    /// <param name="senderPtr">A pointer to the NDI sender instance.</param>
    /// <exception cref="ArgumentException">Thrown if the sender pointer is invalid.</exception>
    public NativeNdiVideoSender(nint senderPtr)
    {
        if (senderPtr == nint.Zero)
        {
            throw new ArgumentException("Invalid NDI sender handle", nameof(senderPtr));
        }

        this.senderPtr = senderPtr;
    }

    /// <summary>
    /// Sends an NDI video frame using the native NDI library.
    /// </summary>
    /// <param name="frame">The video frame to send.</param>
    public void Send(ref NDIlib.video_frame_v2_t frame)
    {
        NDIlib.send_send_video_v2(senderPtr, ref frame);
    }
}
