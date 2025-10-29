using System;

namespace Tractus.HtmlToNdi.Video;

/// <summary>
/// Abstraction for scheduling Chromium invalidation work.
/// </summary>
internal interface IChromiumInvalidator : IDisposable
{
    /// <summary>
    /// Starts the invalidator.
    /// </summary>
    /// <param name="usePacedInvalidation">If <c>true</c>, Chromium invalidations are driven by <see cref="RequestInvalidate"/>; otherwise they follow an internal cadence.</param>
    /// <param name="enableCadenceAlignment">If <c>true</c>, cadence drift measurements influence the invalidate spacing.</param>
    void Start(bool usePacedInvalidation, bool enableCadenceAlignment);

    /// <summary>
    /// Signals that Chromium produced a paint event.
    /// </summary>
    void NotifyPaint();

    /// <summary>
    /// Requests the next Chromium invalidate when paced invalidation is enabled.
    /// </summary>
    void RequestInvalidate();

    /// <summary>
    /// Temporarily suppresses invalidation (periodic and watchdog paths).
    /// </summary>
    void PauseInvalidation();

    /// <summary>
    /// Resumes invalidation after a pause.
    /// </summary>
    void ResumeInvalidation();

    /// <summary>
    /// Updates the cadence drift (in frames) observed by the paced pipeline.
    /// </summary>
    /// <param name="deltaFrames">Positive values indicate Chromium is leading the paced output.</param>
    void UpdateCadenceDrift(double deltaFrames);
}
