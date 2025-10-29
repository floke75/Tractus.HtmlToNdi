namespace Tractus.HtmlToNdi.Video;

/// <summary>
/// Abstraction that coordinates Chromium invalidations with the paced video
/// pipeline so capture can throttle, pause, or realign repaint requests.
/// </summary>
internal interface IChromiumInvalidationScheduler : IDisposable
{
    /// <summary>
    /// Starts accepting pacing hints and issuing invalidations according to the
    /// configured mode.
    /// </summary>
    void Start();

    /// <summary>
    /// Notifies the scheduler that Chromium produced a paint so watchdog and
    /// cadence bookkeeping remain accurate.
    /// </summary>
    void NotifyPaint();

    /// <summary>
    /// Requests that the next Chromium invalidation be issued, typically after
    /// the paced sender transmits or repeats a frame.
    /// </summary>
    void RequestNextInvalidate();

    /// <summary>
    /// Temporarily pauses Chromium invalidations so capture can yield to
    /// backpressure while the paced pipeline drains the buffer.
    /// </summary>
    void PauseInvalidation();

    /// <summary>
    /// Resumes Chromium invalidations after a pause and resets the watchdog
    /// baseline so it does not immediately trigger.
    /// </summary>
    void ResumeInvalidation();

    /// <summary>
    /// Updates the alignment delta (in frames) so the next invalidate can skew
    /// earlier or later to follow capture cadence feedback.
    /// </summary>
    /// <param name="deltaFrames">The desired adjustment in frames.</param>
    void UpdateAlignmentDelta(double deltaFrames);
}
