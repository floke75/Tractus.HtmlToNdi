namespace Tractus.HtmlToNdi.Video;

public readonly struct FramePacerMetrics
{
    public FramePacerMetrics(
        long framesSent,
        long repeatedFrames,
        long droppedFrames,
        double? averageIntervalMilliseconds,
        double? minIntervalMilliseconds,
        double? maxIntervalMilliseconds,
        double targetIntervalMilliseconds)
    {
        this.FramesSent = framesSent;
        this.RepeatedFrames = repeatedFrames;
        this.DroppedFrames = droppedFrames;
        this.AverageIntervalMilliseconds = averageIntervalMilliseconds;
        this.MinIntervalMilliseconds = minIntervalMilliseconds;
        this.MaxIntervalMilliseconds = maxIntervalMilliseconds;
        this.TargetIntervalMilliseconds = targetIntervalMilliseconds;
    }

    public long FramesSent { get; }
    public long RepeatedFrames { get; }
    public long DroppedFrames { get; }
    public double? AverageIntervalMilliseconds { get; }
    public double? MinIntervalMilliseconds { get; }
    public double? MaxIntervalMilliseconds { get; }
    public double TargetIntervalMilliseconds { get; }
}
