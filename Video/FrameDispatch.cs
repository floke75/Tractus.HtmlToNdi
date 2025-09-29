using System;

namespace Tractus.HtmlToNdi.Video;

public readonly struct FrameDispatch
{
    public FrameDispatch(VideoFrame frame, bool isRepeat, int droppedFrames, TimeSpan actualInterval, DateTime dispatchTimeUtc)
    {
        this.Frame = frame ?? throw new ArgumentNullException(nameof(frame));
        this.IsRepeat = isRepeat;
        this.DroppedFrames = droppedFrames;
        this.ActualInterval = actualInterval;
        this.DispatchTimeUtc = dispatchTimeUtc;
    }

    public VideoFrame Frame { get; }

    public bool IsRepeat { get; }

    public int DroppedFrames { get; }

    public TimeSpan ActualInterval { get; }

    public DateTime DispatchTimeUtc { get; }
}