namespace Tractus.HtmlToNdi.Launcher;
/// <summary>
/// Defines the pacing strategy for the video pipeline.
/// </summary>
public enum PacingMode
{
    /// <summary>
    /// Prioritizes strict, low latency at the risk of less smooth
    playback.
    /// </summary>
    Latency,
    /// <summary>
    /// Prioritizes smooth playback by allowing for a deep buffer and
    variable latency.
    /// </summary>
    Smoothness
}
