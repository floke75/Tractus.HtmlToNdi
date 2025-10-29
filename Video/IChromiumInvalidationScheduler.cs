namespace Tractus.HtmlToNdi.Video;

/// <summary>
/// Abstraction that controls how Chromium invalidations are scheduled.
/// </summary>
internal interface IChromiumInvalidationScheduler : IDisposable
{
    /// <summary>
    /// Starts the scheduler.
    /// </summary>
    void Start();

    /// <summary>
    /// Notifies the scheduler that Chromium produced a paint.
    /// </summary>
    void NotifyPaint();

    /// <summary>
    /// Requests that the next Chromium invalidation be issued.
    /// </summary>
    void RequestNextInvalidate();

    /// <summary>
    /// Temporarily pauses Chromium invalidations.
    /// </summary>
    void PauseInvalidation();

    /// <summary>
    /// Resumes Chromium invalidations after a pause.
    /// </summary>
    void ResumeInvalidation();

    /// <summary>
    /// Updates the alignment delta (in frames) to skew the next invalidation deadline.
    /// </summary>
    /// <param name="deltaFrames">The desired adjustment in frames.</param>
    void UpdateAlignmentDelta(double deltaFrames);
}
