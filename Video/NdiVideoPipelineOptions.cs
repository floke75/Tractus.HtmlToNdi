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
    /// Gets or sets a value indicating whether Chromium invalidation should be paced by the send loop.
    /// </summary>
    public bool EnablePacedInvalidation { get; init; }

    /// <summary>
    /// Gets or sets a value indicating whether paced invalidation should be forcefully disabled even when other inputs request it.
    /// </summary>
    public bool DisablePacedInvalidation { get; init; }

    /// <summary>
    /// Gets or sets a value indicating whether capture invalidation should be paused when the buffer is ahead.
    /// </summary>
    public bool EnableCaptureBackpressure { get; init; }

    /// <summary>
    /// Gets or sets a value indicating whether the invalidation cadence should adapt to alignment telemetry.
    /// </summary>
    public bool EnablePumpCadenceAdaptation { get; init; }

    /// <summary>
    /// Gets or sets a value indicating whether compositor-driven capture should be used instead of invalidation-driven paints.
    /// </summary>
    public bool EnableCompositorCapture { get; init; }

    /// <summary>
    /// Gets or sets the pacing mode for the video pipeline.
    /// </summary>
    public Tractus.HtmlToNdi.Launcher.PacingMode PacingMode { get; init; } = Tractus.HtmlToNdi.Launcher.PacingMode.Latency;
}
