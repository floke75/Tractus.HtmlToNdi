namespace Tractus.HtmlToNdi.Video;

internal sealed record NdiVideoPipelineOptions
{
    public bool EnableBuffering { get; init; }

    public int BufferDepth { get; init; } = 3;

    public TimeSpan TelemetryInterval { get; init; } = TimeSpan.FromSeconds(10);
}
