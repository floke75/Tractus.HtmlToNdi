namespace Tractus.HtmlToNdi.FramePacing;

/// <summary>
/// Provides context for a paced frame send.
/// </summary>
public readonly record struct FrameDeliveryContext(
    bool IsRepeat,
    int DroppedFrames,
    TimeSpan Latency,
    int Backlog);
