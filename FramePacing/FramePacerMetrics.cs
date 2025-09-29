namespace Tractus.HtmlToNdi.FramePacing;

public readonly record struct FramePacerMetrics(
    long FramesSent,
    long RepeatedFrames,
    long DroppedFrames,
    double? AverageIntervalMilliseconds,
    double? MinIntervalMilliseconds,
    double? MaxIntervalMilliseconds,
    double? AverageLatencyMilliseconds,
    double? MinLatencyMilliseconds,
    double? MaxLatencyMilliseconds,
    double TargetFps,
    int BufferCapacity);
