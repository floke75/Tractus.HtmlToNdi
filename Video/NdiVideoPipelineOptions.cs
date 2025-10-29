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
    /// Gets or sets a value indicating whether paced invalidation should be driven by the paced sender.
    /// When enabled the pipeline only requests Chromium invalidations after it finishes processing the prior frame.
    /// </summary>
    public bool EnablePacedInvalidation { get; init; }
        = false;

    /// <summary>
    /// Gets or sets a value indicating whether capture invalidation should pause when the buffer is ahead.
    /// Pauses occur after sustained positive latency or a backlog spike and automatically resume once conditions stabilise.
    /// </summary>
    public bool EnableCaptureBackpressure { get; init; }
        = false;

    /// <summary>
    /// Gets or sets a value indicating whether cadence alignment measurements should influence the frame pump.
    /// When enabled the pump adjusts its periodic interval using drift reported by paced capture telemetry.
    /// </summary>
    public bool EnablePumpCadenceAlignment { get; init; }
        = false;
}
