using System;
using System.Runtime.InteropServices;
using NewTek;
using Serilog;

namespace Tractus.HtmlToNdi.Video;

public sealed class NdiVideoFrameSender : IVideoFrameSender
{
    private readonly Func<nint> senderPtrAccessor;
    private readonly Func<(int Numerator, int Denominator)> frameRateAccessor;

    public NdiVideoFrameSender(Func<nint> senderPtrAccessor, Func<(int Numerator, int Denominator)> frameRateAccessor)
    {
        this.senderPtrAccessor = senderPtrAccessor;
        this.frameRateAccessor = frameRateAccessor;
    }

    public void Send(VideoFrameData frame, bool isRepeat, long droppedFrames, TimeSpan frameInterval)
    {
        var senderPtr = this.senderPtrAccessor();
        if (senderPtr == nint.Zero)
        {
            Log.Logger.Warning("Video pacer attempted to send without a valid NDI sender instance.");
            return;
        }

        var handle = GCHandle.Alloc(frame.Pixels, GCHandleType.Pinned);
        try
        {
            var frameRate = this.frameRateAccessor();
            var videoFrame = new NDIlib.video_frame_v2_t
            {
                FourCC = NDIlib.FourCC_type_e.FourCC_type_BGRA,
                frame_rate_N = frameRate.Numerator,
                frame_rate_D = frameRate.Denominator,
                frame_format_type = NDIlib.frame_format_type_e.frame_format_type_progressive,
                line_stride_in_bytes = frame.Stride,
                picture_aspect_ratio = (float)frame.Width / frame.Height,
                p_data = handle.AddrOfPinnedObject(),
                timecode = NDIlib.send_timecode_synthesize,
                xres = frame.Width,
                yres = frame.Height,
            };

            NDIlib.send_send_video_v2(senderPtr, ref videoFrame);
        }
        finally
        {
            handle.Free();
        }
    }
}
