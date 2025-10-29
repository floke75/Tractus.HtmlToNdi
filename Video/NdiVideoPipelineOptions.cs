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
    /// Gets or sets a value indicating whether Chromium invalidations should be paced alongside the NDI sender.
    /// </summary>
    public bool EnablePacedInvalidation { get; init; }

    /// <summary>
    /// Gets or sets a value indicating whether capture should be paused when the backlog exceeds pacing capacity.
    /// </summary>
    public bool EnableCaptureBackpressure { get; init; }

    /// <summary>
    /// Gets or sets a value indicating whether the Chromium pump should follow cadence-alignment telemetry.
    /// </summary>
    public bool EnablePumpAlignment { get; init; }
}
