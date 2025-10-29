namespace Tractus.HtmlToNdi.Video;

/// <summary>
/// Represents the options for the NDI video pipeline.
/// </summary>
internal sealed record NdiVideoPipelineOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether buffering is enabled.
    /// </summary>
    public bool EnableBuffering { get; init; }

    /// <summary>
    /// Gets or sets the buffer depth.
    /// </summary>
    public int BufferDepth { get; init; } = 3;

    /// <summary>
    /// Gets or sets the telemetry interval.
    /// </summary>
    public TimeSpan TelemetryInterval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Gets or sets a value indicating whether latency expansion is allowed.
    /// </summary>
    public bool AllowLatencyExpansion { get; init; }

    /// <summary>
    /// Gets or sets a value indicating whether paced output should align to capture timestamps.
    /// </summary>
    public bool AlignWithCaptureTimestamps { get; init; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether capture/output cadence metrics are logged with telemetry.
    /// </summary>
    public bool EnableCadenceTelemetry { get; init; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the pipeline should pace Chromium invalidations using backlog and latency telemetry.
    /// </summary>
    public bool UsePacedInvalidation { get; init; }
}
