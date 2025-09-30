namespace Tractus.HtmlToNdi.Video;

public sealed class NdiVideoPipelineOptions
{
    public FrameRate NdiFrameRate { get; set; } = new FrameRate(60, 1);

    public bool EnableBufferedOutput { get; set; }

    public int BufferDepth { get; set; } = 3;

    public bool LogFrameStats { get; set; } = true;
}
