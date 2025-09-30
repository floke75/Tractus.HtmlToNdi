namespace Tractus.HtmlToNdi.Video;

public readonly struct FrameOutput
{
    public FrameOutput(FrameMetadata metadata, ReadOnlyMemory<byte> pixelData, bool isRepeat, int droppedFrames, TimeSpan actualInterval)
    {
        this.Metadata = metadata;
        this.PixelData = pixelData;
        this.IsRepeat = isRepeat;
        this.DroppedFrames = droppedFrames;
        this.ActualInterval = actualInterval;
    }

    public FrameMetadata Metadata { get; }
    public ReadOnlyMemory<byte> PixelData { get; }
    public bool IsRepeat { get; }
    public int DroppedFrames { get; }
    public TimeSpan ActualInterval { get; }
}
