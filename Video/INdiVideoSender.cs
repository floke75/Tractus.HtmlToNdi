using NewTek.NDI;

namespace Tractus.HtmlToNdi.Video;

internal interface INdiVideoSender
{
    void Send(ref NDIlib.video_frame_v2_t frame);
}

internal sealed class NativeNdiVideoSender : INdiVideoSender
{
    private readonly nint senderPtr;

    public NativeNdiVideoSender(nint senderPtr)
    {
        if (senderPtr == nint.Zero)
        {
            throw new ArgumentException("Invalid NDI sender handle", nameof(senderPtr));
        }

        this.senderPtr = senderPtr;
    }

    public void Send(ref NDIlib.video_frame_v2_t frame)
    {
        NDIlib.send_send_video_v2(senderPtr, ref frame);
    }
}
