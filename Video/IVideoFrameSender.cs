namespace Tractus.HtmlToNdi.Video;

public interface IVideoFrameSender
{
    void Send(VideoFrameData frame, bool isRepeat, long droppedFrames, TimeSpan frameInterval);
}
