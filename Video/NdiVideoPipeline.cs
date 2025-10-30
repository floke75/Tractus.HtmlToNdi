using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using NewTek;
using NewTek.NDI;
using Serilog;
using Tractus.HtmlToNdi.Chromium;

namespace Tractus.HtmlToNdi.Video;

/// <summary>
/// Manages the paced video pipeline, including the output buffer, Chromium invalidation scheduler, and capture backpressure.
/// </summary>
internal sealed class NdiVideoPipeline : IDisposable
{
    private readonly INdiVideoSender sender;
    private readonly FrameRate configuredFrameRate;
    private readonly NdiVideoPipelineOptions options;
    private readonly FrameTimeAverager timeAverager = new();
    private readonly CancellationTokenSource cancellation = new();
    private readonly FrameRingBuffer<NdiVideoFrame>? ringBuffer;
    private readonly ILogger logger;
    private static readonly TimeSpan BusyWaitThreshold = TimeSpan.FromMilliseconds(1);
    private static readonly double StopwatchTicksToTimeSpanTicks = TimeSpan.TicksPerSecond / (double)Stopwatch.Frequency;

    private readonly int targetDepth;
    private readonly double lowWatermark;
    private readonly double highWatermark;
    private readonly bool allowLatencyExpansion;
    private readonly TimeSpan frameInterval;
    private readonly long maxPacingAdjustmentTicks;
    private readonly TimeSpan invalidationTicketTimeout;
    private readonly TimeSpan captureDemandCheckInterval;
    private readonly Queue<InvalidationTicket> invalidationTickets = new();
    private readonly object invalidationTicketGate = new();

    private bool bufferPrimed;
    private bool isWarmingUp = true;
    private bool hasPrimedOnce;
    private double latencyError;
    private int consecutiveLowBacklogTicks;
    private DateTime warmupStarted;
    private long underruns;
    private long warmupCycles;
    private long lastWarmupDurationTicks;
    private long currentWarmupRepeatTicks;
    private long lastWarmupRepeatTicks;
    private long lowWatermarkHits;
    private long highWatermarkHits;
    private long latencyResyncDrops;
    private bool latencyExpansionActive;
    private long latencyExpansionSessions;
    private long latencyExpansionTicks;
    private long latencyExpansionFramesServed;
    private long captureGatePauses;
    private long captureGateResumes;

    private bool pacingResetRequested;

    private Task? pacingTask;
    private NdiVideoFrame? lastSentFrame;
    private long capturedFrames;
    private long sentFrames;
    private long repeatedFrames;
    private DateTime lastTelemetry = DateTime.UtcNow;
    private readonly CadenceTracker captureCadenceTracker;
    private readonly CadenceTracker outputCadenceTracker;
    private readonly bool alignWithCaptureTimestamps;
    private readonly bool cadenceTelemetryEnabled;
    private readonly bool cadenceTrackingEnabled;
    private double cadenceAlignmentDeltaFrames;
    private readonly bool pacedInvalidationEnabled;
    private readonly bool captureBackpressureEnabled;
    private readonly bool directPacedInvalidationEnabled;
    private readonly bool pumpCadenceAdaptationEnabled;

    private IPacedInvalidationScheduler? invalidationScheduler;
    private bool captureGateActive;
    private int consecutivePositiveLatencyTicks;
    private long lastPacingOffsetTicks;
    private int directInvalidationPending;
    private long pendingInvalidations;
    private long spuriousCaptureCount;
    private long expiredInvalidationTickets;
    private readonly object captureDemandMaintenanceGate = new();
    private CancellationTokenSource? captureDemandMaintenanceCts;
    private Task? captureDemandMaintenanceTask;

    private enum InvalidationTicketOutcome
    {
        Consumed,
        Returned,
        Expired,
    }

    /// <summary>
    /// Represents a single paced invalidation request that tracks whether the slot
    /// was consumed, returned, or expired so the pending request counters remain
    /// accurate.
    /// </summary>
    private sealed class InvalidationTicket
    {
        private const int StateIssued = 0;
        private const int StateConsumed = 1;
        private const int StateReturned = 2;
        private const int StateExpired = 3;

        private readonly NdiVideoPipeline owner;
        private int state;
        private CancellationTokenSource? timeoutCts;

        internal InvalidationTicket(NdiVideoPipeline owner)
        {
            this.owner = owner;
        }

        /// <summary>
        /// Associates a timeout cancellation source so the ticket can expire if it
        /// outlives the configured request window.
        /// </summary>
        internal void ArmTimeout(CancellationTokenSource cts)
        {
            Interlocked.Exchange(ref timeoutCts, cts);
        }

        /// <summary>
        /// Marks the ticket as consumed and notifies the owning pipeline to release
        /// the pending slot.
        /// </summary>
        internal bool TryComplete()
        {
            if (!TryTransition(StateConsumed))
            {
                return false;
            }

            owner.FinalizeTicket(this, InvalidationTicketOutcome.Consumed);
            return true;
        }

        /// <summary>
        /// Returns the ticket to the pool when a request is cancelled before the
        /// scheduler consumes it.
        /// </summary>
        internal bool TryReturn()
        {
            if (!TryTransition(StateReturned))
            {
                return false;
            }

            owner.FinalizeTicket(this, InvalidationTicketOutcome.Returned);
            return true;
        }

        /// <summary>
        /// Expires the ticket after the timeout elapses so stale entries are removed
        /// from the queue.
        /// </summary>
        internal bool TryExpire()
        {
            if (!TryTransition(StateExpired))
            {
                return false;
            }

            owner.FinalizeTicket(this, InvalidationTicketOutcome.Expired);
            return true;
        }

        internal void CancelTimeout()
        {
            var cts = Interlocked.Exchange(ref timeoutCts, null);
            if (cts is null)
            {
                return;
            }

            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
            finally
            {
                cts.Dispose();
            }
        }

        internal bool IsStale => Volatile.Read(ref state) != StateIssued;

        private bool TryTransition(int targetState)
        {
            return Interlocked.CompareExchange(ref state, targetState, StateIssued) == StateIssued;
        }
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="NdiVideoPipeline"/> class.
    /// </summary>
    /// <param name="sender">The NDI video sender.</param>
    /// <param name="frameRate">The configured frame rate.</param>
    /// <param name="options">The pipeline options.</param>
    /// <param name="logger">The logger instance.</param>
    public NdiVideoPipeline(INdiVideoSender sender, FrameRate frameRate, NdiVideoPipelineOptions options, ILogger logger)
    {
        this.sender = sender ?? throw new ArgumentNullException(nameof(sender));
        configuredFrameRate = frameRate;
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.logger = logger;

        targetDepth = Math.Max(1, options.BufferDepth);
        lowWatermark = Math.Max(0, targetDepth - 1.5);
        highWatermark = targetDepth + 1;
        allowLatencyExpansion = options.AllowLatencyExpansion && options.EnableBuffering;
        frameInterval = frameRate.FrameDuration;
        maxPacingAdjustmentTicks = Math.Max(1, frameInterval.Ticks / 2);
        invalidationTicketTimeout = CalculateInvalidationTicketTimeout(frameInterval);
        captureDemandCheckInterval = CalculateCaptureDemandCheckInterval(frameInterval, invalidationTicketTimeout);
        alignWithCaptureTimestamps = options.AlignWithCaptureTimestamps;
        cadenceTelemetryEnabled = options.EnableCadenceTelemetry;
        cadenceTrackingEnabled = alignWithCaptureTimestamps || cadenceTelemetryEnabled;
        pacedInvalidationEnabled = options.EnablePacedInvalidation;
        captureBackpressureEnabled = options.EnableBuffering
            && options.EnableCaptureBackpressure
            && options.EnablePacedInvalidation;
        directPacedInvalidationEnabled = !options.EnableBuffering && options.EnablePacedInvalidation;
        if (options.EnableBuffering && options.EnableCaptureBackpressure && !options.EnablePacedInvalidation)
        {
            logger.Warning(
                "Capture backpressure requires paced invalidation; disabling backpressure until paced invalidation is enabled.");
        }
        pumpCadenceAdaptationEnabled = options.EnablePumpCadenceAdaptation;
        captureCadenceTracker = new CadenceTracker(frameInterval);
        outputCadenceTracker = new CadenceTracker(frameInterval);

        if (options.EnableBuffering)
        {
            ringBuffer = new FrameRingBuffer<NdiVideoFrame>(targetDepth + 1);
            warmupStarted = DateTime.UtcNow;
        }
        else
        {
            bufferPrimed = true;
            isWarmingUp = false;
        }
    }

    /// <summary>
    /// Gets a value indicating whether buffering is enabled.
    /// </summary>
    public bool BufferingEnabled => options.EnableBuffering;

    /// <summary>
    /// Gets the configured frame rate.
    /// </summary>
    public FrameRate FrameRate => configuredFrameRate;

    internal NdiVideoPipelineOptions Options => options;

    /// <summary>
    /// Attaches the pacing-aware invalidation scheduler and resets gating state so telemetry stays in sync.
    /// </summary>
    /// <param name="scheduler">The scheduler responsible for coordinating Chromium invalidations.</param>
    internal void AttachInvalidationScheduler(IPacedInvalidationScheduler? scheduler)
    {
        invalidationScheduler = scheduler;
        captureGateActive = false;
        consecutivePositiveLatencyTicks = 0;
        ResetInvalidationTickets();
        Interlocked.Exchange(ref pendingInvalidations, 0);
        Interlocked.Exchange(ref spuriousCaptureCount, 0);
        Interlocked.Exchange(ref expiredInvalidationTickets, 0);

        if (scheduler is null)
        {
            StopCaptureDemandMaintenance();
            return;
        }

        scheduler.UpdateCadenceAlignment(0);
        StartCaptureDemandMaintenance();

        if (directPacedInvalidationEnabled)
        {
            Interlocked.Exchange(ref directInvalidationPending, 0);
            RequestDirectInvalidation();
            return;
        }

        if (BufferingEnabled && pacedInvalidationEnabled)
        {
            EnsureCaptureDemand();
        }
    }

    /// <summary>
    /// Starts the paced sender loop when buffering is enabled and primes the scheduler for warm-up.
    /// </summary>
    public void Start()
    {
        if (!BufferingEnabled || pacingTask != null)
        {
            return;
        }

        ResetBufferingState();
        Volatile.Write(ref pacingResetRequested, true);
        pacingTask = Task.Factory.StartNew(
                () => RunPacedLoopAsync(cancellation.Token),
                CancellationToken.None,
                TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default)
            .Unwrap();
    }

    /// <summary>
    /// Stops the video pipeline.
    /// </summary>
    public void Stop()
    {
        cancellation.Cancel();
        StopCaptureDemandMaintenance();
        try
        {
            pacingTask?.Wait();
        }
        catch (Exception ex) when (ex is TaskCanceledException || ex is AggregateException)
        {
            // ignore
        }

        pacingTask = null;
        bufferPrimed = false;
        isWarmingUp = true;
        Interlocked.Exchange(ref directInvalidationPending, 0);
        ResetInvalidationTickets();
        Interlocked.Exchange(ref pendingInvalidations, 0);
    }

    /// <summary>
    /// Handles a captured video frame, either sending it immediately (direct mode) or queueing it for paced delivery.
    /// </summary>
    /// <param name="frame">The captured video frame.</param>
    public void HandleFrame(CapturedFrame frame)
    {
        Interlocked.Increment(ref capturedFrames);
        if (!TryConsumePendingInvalidationTicket())
        {
            Interlocked.Increment(ref spuriousCaptureCount);
            return;
        }

        if (cadenceTrackingEnabled)
        {
            captureCadenceTracker.Record(frame.MonotonicTimestamp);
        }

        if (!BufferingEnabled)
        {
            if (directPacedInvalidationEnabled)
            {
                Interlocked.Exchange(ref directInvalidationPending, 0);
            }

            SendDirect(frame);
            RequestDirectInvalidation();
            return;
        }

        if (ringBuffer is null)
        {
            return;
        }

        var copy = NdiVideoFrame.CopyFrom(frame);
        ringBuffer.Enqueue(copy, out var dropped);
        dropped?.Dispose();
        EmitTelemetryIfNeeded();
        EnsureCaptureDemand(ringBuffer.Count);
    }

    private async Task RunPacedLoopAsync(CancellationToken token)
    {
        var pacingClock = Stopwatch.StartNew();
        var pacingOrigin = pacingClock.Elapsed;
        long pacingSequence = 0;

        while (!token.IsCancellationRequested)
        {
            EnsureCaptureDemand();

            if (Volatile.Read(ref pacingResetRequested))
            {
                pacingOrigin = pacingClock.Elapsed;
                pacingSequence = 0;
                Volatile.Write(ref pacingResetRequested, false);
            }

            var nextSequence = pacingSequence + 1;
            var deadline = CalculateNextDeadline(pacingOrigin, nextSequence);

            await WaitUntilAsync(pacingClock, deadline, token);

            if (token.IsCancellationRequested)
            {
                break;
            }

            var sent = TrySendBufferedFrame();
            if (!sent && lastSentFrame is not null)
            {
                RepeatLastFrame();
            }

            pacingSequence = nextSequence;
        }
    }

    private TimeSpan CalculateNextDeadline(TimeSpan origin, long nextSequence)
    {
        Volatile.Write(ref lastPacingOffsetTicks, 0);
        var baseline = origin + TimeSpan.FromTicks(frameInterval.Ticks * nextSequence);

        if (!BufferingEnabled || ringBuffer is null)
        {
            return baseline;
        }

        if (!bufferPrimed && !latencyExpansionActive)
        {
            return baseline;
        }

        var backlog = ringBuffer.Count;
        if (backlog <= 0)
        {
            return baseline;
        }

        var backlogError = targetDepth - backlog;
        var normalized = Math.Clamp(backlogError / (double)targetDepth, -1.5d, 1.5d);
        var integral = Math.Clamp(-Volatile.Read(ref latencyError) / Math.Max(1d, targetDepth), -2d, 2d);
        var adjustmentFactor = normalized + (integral * 0.2d);
        var cadenceAdjustment = CalculateCadenceAlignmentOffset();
        adjustmentFactor += cadenceAdjustment;
        if (Math.Abs(adjustmentFactor) < 1e-6)
        {
            return baseline;
        }

        var adjustmentTicks = (long)Math.Clamp(adjustmentFactor * frameInterval.Ticks, -maxPacingAdjustmentTicks, maxPacingAdjustmentTicks);

        if (!bufferPrimed && latencyExpansionActive && backlog < targetDepth)
        {
            var backlogDeficit = targetDepth - backlog;
            if (backlogDeficit > 0)
            {
                var requiredTicks = frameInterval.Ticks * backlogDeficit;
                if (requiredTicks > adjustmentTicks)
                {
                    adjustmentTicks = requiredTicks;
                }
            }
        }

        Volatile.Write(ref lastPacingOffsetTicks, adjustmentTicks);
        return baseline + TimeSpan.FromTicks(adjustmentTicks);
    }

    private static TimeSpan CalculateInvalidationTicketTimeout(TimeSpan frameDuration)
    {
        var minTimeout = TimeSpan.FromMilliseconds(40);
        var maxTimeout = TimeSpan.FromMilliseconds(200);
        var desiredTicks = frameDuration.Ticks * 3L;
        desiredTicks = Math.Max(minTimeout.Ticks, desiredTicks);
        desiredTicks = Math.Min(maxTimeout.Ticks, desiredTicks);
        return TimeSpan.FromTicks(desiredTicks);
    }

    private static TimeSpan CalculateCaptureDemandCheckInterval(TimeSpan frameDuration, TimeSpan ticketTimeout)
    {
        var minIntervalTicks = TimeSpan.FromMilliseconds(5).Ticks;
        var maxIntervalTicks = TimeSpan.FromMilliseconds(100).Ticks;
        var frameTicks = Math.Max(1L, frameDuration.Ticks);
        var timeoutTicks = Math.Max(1L, ticketTimeout.Ticks / 2);
        var candidateTicks = Math.Min(frameTicks, timeoutTicks);
        candidateTicks = Math.Max(minIntervalTicks, candidateTicks);
        candidateTicks = Math.Min(maxIntervalTicks, candidateTicks);
        return TimeSpan.FromTicks(candidateTicks);
    }

    /// <summary>
    /// Refills Chromium invalidation demand using the latest buffer depth so pending
    /// ticket counts stay aligned with the paced sender.
    /// </summary>
    private void EnsureCaptureDemand()
    {
        EnsureCaptureDemandInternal(ringBuffer?.Count);
    }

    /// <summary>
    /// Refills Chromium invalidation demand using the supplied backlog value.
    /// </summary>
    private void EnsureCaptureDemand(int backlog)
    {
        EnsureCaptureDemandInternal(backlog);
    }

    /// <summary>
    /// Issues enough invalidation requests to match the desired pending ticket count
    /// while respecting capture gates and scheduler state.
    /// </summary>
    private void EnsureCaptureDemandInternal(int? backlog)
    {
        if (!pacedInvalidationEnabled)
        {
            return;
        }

        var scheduler = invalidationScheduler;
        if (scheduler is null || captureGateActive || scheduler.IsPaused)
        {
            return;
        }

        if (!BufferingEnabled || backlog is null || ringBuffer is null)
        {
            return;
        }

        var desired = CalculateDesiredPendingInvalidations(backlog.Value);
        var current = Volatile.Read(ref pendingInvalidations);
        while (current < desired)
        {
            var request = RequestInvalidateWithTicketAsync(scheduler, cancellation.Token);
            ObserveInvalidationRequest(request, "Failed to request Chromium invalidation while refilling demand");

            var updated = Volatile.Read(ref pendingInvalidations);
            if (updated <= current)
            {
                break;
            }

            current = updated;
        }
    }

    /// <summary>
    /// Starts the watchdog that periodically replenishes direct pacing demand so the
    /// scheduler keeps driving Chromium even if sends stall.
    /// </summary>
    private void StartCaptureDemandMaintenance()
    {
        if (!directPacedInvalidationEnabled || !CaptureTicketsEnabled)
        {
            return;
        }

        lock (captureDemandMaintenanceGate)
        {
            if (captureDemandMaintenanceTask is not null && !captureDemandMaintenanceTask.IsCompleted)
            {
                return;
            }

            captureDemandMaintenanceCts?.Cancel();
            captureDemandMaintenanceCts?.Dispose();

            var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
            captureDemandMaintenanceCts = linked;
            captureDemandMaintenanceTask = Task.Run(() => RunCaptureDemandMaintenanceAsync(linked.Token));
        }
    }

    /// <summary>
    /// Stops the capture demand watchdog, cancelling the maintenance loop and wiring fault logging for any outstanding task.
    /// </summary>
    private void StopCaptureDemandMaintenance()
    {
        CancellationTokenSource? cts;
        Task? task;

        lock (captureDemandMaintenanceGate)
        {
            cts = captureDemandMaintenanceCts;
            task = captureDemandMaintenanceTask;
            captureDemandMaintenanceCts = null;
            captureDemandMaintenanceTask = null;
        }

        if (cts is not null)
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
            finally
            {
                cts.Dispose();
            }
        }

        if (task is not null)
        {
            _ = task.ContinueWith(
                t =>
                {
                    if (!t.IsFaulted || t.Exception is null)
                    {
                        return;
                    }

                    var root = t.Exception.GetBaseException() ?? t.Exception;
                    if (root is OperationCanceledException || root is ObjectDisposedException)
                    {
                        return;
                    }

                    logger.Warning(root, "Capture demand maintenance task faulted");
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    /// <summary>
    /// Periodically re-checks pending invalidation demand while direct pacing is active
    /// so Chromium continues to receive capture requests.
    /// </summary>
    private async Task RunCaptureDemandMaintenanceAsync(CancellationToken token)
    {
        var interval = captureDemandCheckInterval > TimeSpan.Zero
            ? captureDemandCheckInterval
            : TimeSpan.FromMilliseconds(10);

        try
        {
            using var timer = new PeriodicTimer(interval);
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                if (token.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    EnsureCaptureDemand();
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.Warning(ex, "Capture demand maintenance tick failed");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// Computes the number of invalidation requests that should remain outstanding for
    /// the current backlog and pacing mode.
    /// </summary>
    private int CalculateDesiredPendingInvalidations(int backlog)
    {
        var desired = targetDepth - backlog;
        if (desired < 1)
        {
            desired = 1;
        }

        if (!bufferPrimed || isWarmingUp || latencyExpansionActive)
        {
            if (desired < targetDepth)
            {
                desired = targetDepth;
            }
        }

        var limit = GetPendingInvalidationLimit();
        if (desired > limit)
        {
            desired = limit;
        }

        return desired;
    }

    /// <summary>
    /// Hooks continuations to report unexpected failures from asynchronous invalidation
    /// requests without throwing on the pacing path.
    /// </summary>
    private void ObserveInvalidationRequest(Task request, string warningMessage)
    {
        if (request.IsCompleted)
        {
            if (request.IsFaulted)
            {
                var ex = request.Exception?.GetBaseException() ?? request.Exception;
                if (ex is not OperationCanceledException && ex is not ObjectDisposedException)
                {
                    logger.Warning(ex, warningMessage);
                }
            }

            return;
        }

        _ = request.ContinueWith(
            static (task, state) =>
            {
                if (!task.IsFaulted)
                {
                    return;
                }

                var (logger, message) = ((ILogger Logger, string Message))state!;
                var root = task.Exception?.GetBaseException() ?? task.Exception;
                if (root is OperationCanceledException || root is ObjectDisposedException)
                {
                    return;
                }

                logger.Warning(root, message);
            },
            (logger, warningMessage),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>
    /// Gets a value indicating whether paced invalidation tickets should be tracked for the
    /// current configuration. Direct pacing always uses a single ticket, while buffered pacing
    /// only enables tickets when Chromium invalidations are coordinated with the output queue.
    /// </summary>
    private bool CaptureTicketsEnabled => directPacedInvalidationEnabled || (BufferingEnabled && pacedInvalidationEnabled);

    /// <summary>
    /// Requests an invalidation from the scheduler while holding an invalidation
    /// ticket so pending counters stay in sync even if the request faults.
    /// </summary>
    private async Task RequestInvalidateWithTicketAsync(IPacedInvalidationScheduler scheduler, CancellationToken token)
    {
        if (!TryAcquireInvalidationSlot(out var ticket))
        {
            return;
        }

        try
        {
            await scheduler.RequestInvalidateAsync(token).ConfigureAwait(false);
        }
        catch
        {
            ReturnInvalidationTicket(ticket);
            throw;
        }
    }

    /// <summary>
    /// Attempts to reserve a pacing slot for an upcoming invalidation request.
    /// </summary>
    private bool TryAcquireInvalidationSlot(out InvalidationTicket? ticket)
    {
        ticket = null;
        if (!CaptureTicketsEnabled)
        {
            return true;
        }

        var limit = GetPendingInvalidationLimit();
        while (true)
        {
            var pending = Volatile.Read(ref pendingInvalidations);
            if (pending >= limit)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref pendingInvalidations, pending + 1, pending) != pending)
            {
                continue;
            }

            var newTicket = new InvalidationTicket(this);
            lock (invalidationTicketGate)
            {
                invalidationTickets.Enqueue(newTicket);
            }
            ScheduleTicketExpiration(newTicket);
            ticket = newTicket;
            return true;
        }
    }

    /// <summary>
    /// Calculates how many invalidation requests may be outstanding based on the
    /// current buffering state.
    /// </summary>
    private int GetPendingInvalidationLimit()
    {
        if (!CaptureTicketsEnabled)
        {
            return int.MaxValue;
        }

        if (directPacedInvalidationEnabled)
        {
            return 1;
        }

        if (!BufferingEnabled)
        {
            return 1;
        }

        var primed = Volatile.Read(ref bufferPrimed);
        var warming = Volatile.Read(ref isWarmingUp);
        var expanding = Volatile.Read(ref latencyExpansionActive);

        if (!primed || warming || expanding)
        {
            return Math.Max(2, targetDepth + 1);
        }

        return Math.Max(1, targetDepth);
    }

    /// <summary>
    /// Consumes the next non-stale invalidation ticket, ensuring expired entries
    /// are discarded before a new capture proceeds.
    /// </summary>
    private bool TryConsumePendingInvalidationTicket()
    {
        if (!CaptureTicketsEnabled)
        {
            return true;
        }

        while (true)
        {
            InvalidationTicket? ticket = null;

            lock (invalidationTicketGate)
            {
                while (invalidationTickets.Count > 0)
                {
                    var head = invalidationTickets.Peek();
                    if (head.IsStale)
                    {
                        invalidationTickets.Dequeue();
                        continue;
                    }

                    ticket = invalidationTickets.Dequeue();
                    break;
                }
            }

            if (ticket is null)
            {
                return false;
            }

            if (ticket.TryComplete())
            {
                return true;
            }
        }
    }

    /// <summary>
    /// Returns a ticket to the pool when an invalidation fails before completion.
    /// </summary>
    private void ReturnInvalidationTicket(InvalidationTicket? ticket)
    {
        if (!CaptureTicketsEnabled || ticket is null)
        {
            return;
        }

        ticket.TryReturn();
    }

    /// <summary>
    /// Arms a timeout for the ticket so stalled invalidation requests are expired
    /// and removed from the queue.
    /// </summary>
    private void ScheduleTicketExpiration(InvalidationTicket ticket)
    {
        if (!CaptureTicketsEnabled)
        {
            return;
        }

        var timeout = invalidationTicketTimeout;
        if (timeout <= TimeSpan.Zero)
        {
            return;
        }

        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
        ticket.ArmTimeout(linkedCts);

        _ = Task.Run(async () =>
        {
            var shouldExpire = true;
            try
            {
                await Task.Delay(timeout, linkedCts.Token).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                shouldExpire = false;
            }
            catch (OperationCanceledException)
            {
                shouldExpire = false;
            }
            catch (ObjectDisposedException)
            {
                shouldExpire = false;
            }
            finally
            {
                try
                {
                    linkedCts.Dispose();
                }
                catch (ObjectDisposedException)
                {
                }
            }

            if (shouldExpire)
            {
                _ = ticket.TryExpire();
            }
        });
    }

    /// <summary>
    /// Releases the ticket, updates bookkeeping counters, and removes stale entries
    /// from the queue based on the final outcome.
    /// </summary>
    private void FinalizeTicket(InvalidationTicket ticket, InvalidationTicketOutcome outcome)
    {
        ticket.CancelTimeout();

        while (true)
        {
            var pending = Volatile.Read(ref pendingInvalidations);
            if (pending <= 0)
            {
                break;
            }

            if (Interlocked.CompareExchange(ref pendingInvalidations, pending - 1, pending) == pending)
            {
                break;
            }
        }

        RemoveInvalidationTicket(ticket);

        if (outcome == InvalidationTicketOutcome.Expired)
        {
            Interlocked.Increment(ref expiredInvalidationTickets);
            HandleExpiredTicket();
        }
    }

    /// <summary>
    /// Removes the specified ticket from the pending queue, trimming any stale entries
    /// so subsequent dequeues see only live requests.
    /// </summary>
    private void RemoveInvalidationTicket(InvalidationTicket ticket)
    {
        if (!CaptureTicketsEnabled)
        {
            return;
        }

        lock (invalidationTicketGate)
        {
            if (invalidationTickets.Count == 0)
            {
                return;
            }

            if (ReferenceEquals(invalidationTickets.Peek(), ticket))
            {
                _ = invalidationTickets.Dequeue();
            }
            else
            {
                List<InvalidationTicket>? survivors = null;

                while (invalidationTickets.Count > 0)
                {
                    var current = invalidationTickets.Dequeue();
                    if (ReferenceEquals(current, ticket) || current.IsStale)
                    {
                        continue;
                    }

                    survivors ??= new List<InvalidationTicket>(invalidationTickets.Count + 1);
                    survivors.Add(current);
                }

                if (survivors is null)
                {
                    return;
                }

                foreach (var survivor in survivors)
                {
                    invalidationTickets.Enqueue(survivor);
                }
            }

            while (invalidationTickets.Count > 0 && invalidationTickets.Peek().IsStale)
            {
                invalidationTickets.Dequeue();
            }
        }
    }

    /// <summary>
    /// Restores capture demand when a pending invalidation exceeds the timeout,
    /// ensuring the pipeline keeps driving Chromium after stalls.
    /// </summary>
    private void HandleExpiredTicket()
    {
        if (!pacedInvalidationEnabled || cancellation.IsCancellationRequested)
        {
            return;
        }

        if (directPacedInvalidationEnabled)
        {
            Interlocked.Exchange(ref directInvalidationPending, 0);
            RequestDirectInvalidation();
            return;
        }

        if (BufferingEnabled && pacedInvalidationEnabled)
        {
            EnsureCaptureDemand(ringBuffer?.Count ?? 0);
            return;
        }

        EnsureCaptureDemand();
    }

    /// <summary>
    /// Drains and returns all outstanding tickets when the pipeline resets so no
    /// stale entries linger in the queue.
    /// </summary>
    private void ResetInvalidationTickets()
    {
        if (!CaptureTicketsEnabled)
        {
            return;
        }

        List<InvalidationTicket>? snapshot = null;

        lock (invalidationTicketGate)
        {
            if (invalidationTickets.Count == 0)
            {
                return;
            }

            snapshot = new List<InvalidationTicket>(invalidationTickets.Count);
            while (invalidationTickets.Count > 0)
            {
                snapshot.Add(invalidationTickets.Dequeue());
            }
        }

        if (snapshot is null)
        {
            return;
        }

        foreach (var ticket in snapshot)
        {
            ticket.TryReturn();
        }
    }

    /// <summary>
    /// Ensures the next direct-send capture is queued once the current frame has been transmitted.
    /// </summary>
    private void RequestDirectInvalidation()
    {
        if (!directPacedInvalidationEnabled)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref directInvalidationPending, 1, 0) != 0)
        {
            return;
        }

        var scheduler = invalidationScheduler;
        if (scheduler is null)
        {
            Interlocked.Exchange(ref directInvalidationPending, 0);
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await RequestInvalidateWithTicketAsync(scheduler, cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Interlocked.Exchange(ref directInvalidationPending, 0);
            }
            catch (ObjectDisposedException)
            {
                Interlocked.Exchange(ref directInvalidationPending, 0);
            }
            catch (Exception ex)
            {
                logger.Warning(ex, "Failed to request Chromium invalidation for direct pacing");
                Interlocked.Exchange(ref directInvalidationPending, 0);
            }
        });
    }

    private void UpdateCaptureBackpressure(int backlog)
    {
        if (!captureBackpressureEnabled)
        {
            return;
        }

        var scheduler = invalidationScheduler;
        if (scheduler is null)
        {
            return;
        }

        if (latencyError > 0.5d)
        {
            if (consecutivePositiveLatencyTicks < int.MaxValue)
            {
                consecutivePositiveLatencyTicks++;
            }
        }
        else if (latencyError <= 0)
        {
            consecutivePositiveLatencyTicks = 0;
        }

        var highBacklog = backlog >= highWatermark;
        if (!captureGateActive && (highBacklog || consecutivePositiveLatencyTicks >= 3))
        {
            captureGateActive = true;
            scheduler.Pause();
            Interlocked.Increment(ref captureGatePauses);
            logger.Information(
                "Chromium capture paused due to oversupply: backlog={Backlog}, latencyError={LatencyError:F2}",
                backlog,
                latencyError);
            return;
        }

        if (captureGateActive && backlog <= targetDepth && latencyError <= 0)
        {
            captureGateActive = false;
            consecutivePositiveLatencyTicks = 0;
            scheduler.Resume();
            Interlocked.Increment(ref captureGateResumes);
            logger.Information(
                "Chromium capture resumed after oversupply: backlog={Backlog}, latencyError={LatencyError:F2}",
                backlog,
                latencyError);
            EnsureCaptureDemand();
        }
    }

    private double CalculateCadenceAlignmentOffset()
    {
        if (!alignWithCaptureTimestamps || !cadenceTrackingEnabled)
        {
            Interlocked.Exchange(ref cadenceAlignmentDeltaFrames, 0d);
            return 0d;
        }

        var captureDrift = captureCadenceTracker.GetDriftFrames();
        var outputDrift = outputCadenceTracker.GetDriftFrames();
        var delta = captureDrift - outputDrift;
        Interlocked.Exchange(ref cadenceAlignmentDeltaFrames, delta);

        if (pumpCadenceAdaptationEnabled && invalidationScheduler is not null)
        {
            invalidationScheduler.UpdateCadenceAlignment(delta);
        }

        if (double.IsNaN(delta) || Math.Abs(delta) < 0.01d)
        {
            return 0d;
        }

        return Math.Clamp(delta * 0.15d, -0.75d, 0.75d);
    }

    private static async Task WaitUntilAsync(Stopwatch clock, TimeSpan deadline, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var remaining = deadline - clock.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                return;
            }

            if (remaining <= BusyWaitThreshold)
            {
                while (deadline > clock.Elapsed)
                {
                    token.ThrowIfCancellationRequested();
                    Thread.SpinWait(64);
                }

                return;
            }

            var sleep = remaining - BusyWaitThreshold;
            if (sleep < TimeSpan.FromMilliseconds(1))
            {
                sleep = TimeSpan.FromMilliseconds(1);
            }

            try
            {
                await Task.Delay(sleep, token);
            }
            catch (TaskCanceledException)
            {
                return;
            }
        }
    }

    private bool TrySendBufferedFrame()
    {
        if (ringBuffer is null)
        {
            return false;
        }

        var backlog = ringBuffer.Count;
        var delta = backlog - targetDepth;
        var integratorUpdated = false;
        var allowSendWhileWarming = false;

        if (isWarmingUp)
        {
            latencyError += delta;
            integratorUpdated = true;

            if (latencyError < -targetDepth)
            {
                latencyError = -targetDepth;
            }

            if (latencyExpansionActive && allowLatencyExpansion && backlog > 0)
            {
                if (backlog >= targetDepth)
                {
                    if (latencyError < 0)
                    {
                        latencyError = 0;
                    }

                    ExitWarmup();
                    backlog = ringBuffer.Count;
                    delta = backlog - targetDepth;
                }
                else
                {
                    allowSendWhileWarming = true;
                    Interlocked.Increment(ref latencyExpansionTicks);
                }
            }
            else if (backlog >= targetDepth)
            {
                if (latencyError < 0)
                {
                    latencyError = 0;
                }

                ExitWarmup();
                backlog = ringBuffer.Count;
                delta = backlog - targetDepth;
            }
            else
            {
                if (latencyExpansionActive && backlog <= 0)
                {
                    latencyExpansionActive = false;
                }
                return false;
            }
        }

        if (backlog <= lowWatermark)
        {
            if (allowLatencyExpansion && backlog > 0)
            {
                if (!latencyExpansionActive)
                {
                    Interlocked.Increment(ref lowWatermarkHits);
                    EnterWarmup(preserveBufferedFrames: true);
                    allowSendWhileWarming = true;
                    Interlocked.Increment(ref latencyExpansionTicks);
                }

                consecutiveLowBacklogTicks = 0;
            }
            else
            {
                consecutiveLowBacklogTicks++;
                if (consecutiveLowBacklogTicks >= 2 || backlog == 0)
                {
                    Interlocked.Increment(ref lowWatermarkHits);
                    EnterWarmup();
                    return false;
                }
            }
        }
        else
        {
            consecutiveLowBacklogTicks = 0;
        }

        if (!integratorUpdated)
        {
            latencyError += delta;
            if (latencyError < -targetDepth)
            {
                latencyError = -targetDepth;
            }
        }

        if (backlog >= highWatermark)
        {
            Interlocked.Increment(ref highWatermarkHits);
        }

        if (latencyError > 1)
        {
            var trimFloor = latencyExpansionActive ? highWatermark : targetDepth;
            var droppedThisTick = 0;

            while (latencyError > 1 && ringBuffer.Count > trimFloor)
            {
                if (!ringBuffer.TryDequeueAsStale(out var dropped) || dropped is null)
                {
                    break;
                }

                dropped.Dispose();
                latencyError -= 1;
                droppedThisTick++;
                Interlocked.Increment(ref latencyResyncDrops);
            }

            if (droppedThisTick > 0)
            {
                backlog = ringBuffer.Count;
                delta = backlog - targetDepth;

                if (!latencyExpansionActive)
                {
                    consecutiveLowBacklogTicks = 0;
                }
            }
        }

        UpdateCaptureBackpressure(backlog);

        if (ringBuffer.TryDequeue(out var frame) && frame is not null)
        {
            SendBufferedFrame(frame);
            EnsureCaptureDemand(ringBuffer.Count);
            if (latencyExpansionActive || allowSendWhileWarming)
            {
                Interlocked.Increment(ref latencyExpansionFramesServed);
            }
            return true;
        }

        EnterWarmup();
        return false;
    }

    private void SendDirect(CapturedFrame frame)
    {
        if (frame.Buffer == IntPtr.Zero)
        {
            return;
        }

        var timestamp = frame.TimestampUtc != default ? frame.TimestampUtc : DateTime.UtcNow;
        var (numerator, denominator) = ResolveFrameRate(timestamp);

        var ndiFrame = CreateVideoFrame(frame, numerator, denominator);
        sender.Send(ref ndiFrame);
        Interlocked.Increment(ref sentFrames);
        if (cadenceTrackingEnabled)
        {
            outputCadenceTracker.Record(Stopwatch.GetTimestamp());
        }
        EmitTelemetryIfNeeded();
    }

    private void SendBufferedFrame(NdiVideoFrame frame)
    {
        var (numerator, denominator) = ResolveFrameRate(frame.Timestamp);

        var ndiFrame = CreateVideoFrame(frame, numerator, denominator);
        sender.Send(ref ndiFrame);

        Interlocked.Increment(ref sentFrames);
        if (cadenceTrackingEnabled)
        {
            outputCadenceTracker.Record(Stopwatch.GetTimestamp());
        }

        lastSentFrame?.Dispose();
        lastSentFrame = frame;

        EmitTelemetryIfNeeded();
    }

    private void RepeatLastFrame()
    {
        if (lastSentFrame is null)
        {
            return;
        }

        if (isWarmingUp)
        {
            Interlocked.Increment(ref currentWarmupRepeatTicks);
        }

        var ndiFrame = CreateVideoFrame(lastSentFrame, configuredFrameRate.Numerator, configuredFrameRate.Denominator);
        sender.Send(ref ndiFrame);
        Interlocked.Increment(ref repeatedFrames);
        if (cadenceTrackingEnabled)
        {
            outputCadenceTracker.Record(Stopwatch.GetTimestamp());
        }
        EmitTelemetryIfNeeded();
    }

    private void EnterWarmup(bool preserveBufferedFrames = false)
    {
        if (!BufferingEnabled || ringBuffer is null)
        {
            return;
        }

        var backlog = ringBuffer.Count;
        var preserving = preserveBufferedFrames && allowLatencyExpansion && backlog > 0;

        if (!isWarmingUp && hasPrimedOnce && lastSentFrame is not null)
        {
            Interlocked.Increment(ref underruns);

            if (preserving)
            {
                logger.Information(
                    "NDI pacer entering latency expansion: buffered={Buffered}, latencyError={LatencyError:F2}",
                    backlog,
                    latencyError);
            }
            else
            {
                logger.Warning(
                    "NDI pacer underrun detected: buffered={Buffered}, latencyError={LatencyError:F2}, preservingBufferedFrames={Preserving}",
                    backlog,
                    latencyError,
                    preserving);
            }
        }

        bufferPrimed = false;
        isWarmingUp = true;
        warmupStarted = DateTime.UtcNow;
        consecutiveLowBacklogTicks = 0;
        Interlocked.Exchange(ref currentWarmupRepeatTicks, 0);

        latencyExpansionActive = preserving;
        if (latencyExpansionActive)
        {
            Interlocked.Increment(ref latencyExpansionSessions);
        }
        else
        {
            ringBuffer.TrimToSingleLatest();
        }

        var clampCeiling = latencyExpansionActive ? targetDepth : 0;
        latencyError = Math.Clamp(latencyError, -targetDepth, clampCeiling);
        Volatile.Write(ref pacingResetRequested, true);
        outputCadenceTracker.Reset();
        Interlocked.Exchange(ref cadenceAlignmentDeltaFrames, 0d);
        invalidationScheduler?.UpdateCadenceAlignment(0);
        EnsureCaptureDemand();
    }

    private void ExitWarmup()
    {
        if (!isWarmingUp)
        {
            return;
        }

        isWarmingUp = false;
        bufferPrimed = true;
        hasPrimedOnce = true;
        consecutiveLowBacklogTicks = 0;
        latencyExpansionActive = false;

        var now = DateTime.UtcNow;
        var duration = now - warmupStarted;
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        warmupStarted = now;
        Interlocked.Increment(ref warmupCycles);
        Interlocked.Exchange(ref lastWarmupDurationTicks, duration.Ticks);
        Interlocked.Exchange(ref lastWarmupRepeatTicks, Interlocked.Exchange(ref currentWarmupRepeatTicks, 0));

        logger.Information(
            "NDI pacer resumed: buffered={Buffered}, warmupMs={WarmupMs:F1}, repeats={Repeats}, latencyError={LatencyError:F2}, latencyExpansionTicks={LatencyExpansionTicks}, latencyExpansionFrames={LatencyExpansionFrames}",
            ringBuffer?.Count ?? 0,
            duration.TotalMilliseconds,
            Interlocked.Read(ref lastWarmupRepeatTicks),
            latencyError,
            Interlocked.Read(ref latencyExpansionTicks),
            Interlocked.Read(ref latencyExpansionFramesServed));

        Volatile.Write(ref pacingResetRequested, true);
        if (BufferingEnabled && pacedInvalidationEnabled)
        {
            EnsureCaptureDemand();
        }
    }

    private void ResetBufferingState()
    {
        captureCadenceTracker.Reset();
        outputCadenceTracker.Reset();
        Interlocked.Exchange(ref cadenceAlignmentDeltaFrames, 0d);
        ResetInvalidationTickets();
        Interlocked.Exchange(ref pendingInvalidations, 0);
        Interlocked.Exchange(ref spuriousCaptureCount, 0);
        Interlocked.Exchange(ref expiredInvalidationTickets, 0);
        bufferPrimed = false;
        isWarmingUp = true;
        hasPrimedOnce = false;
        warmupStarted = DateTime.UtcNow;
        latencyError = 0;
        consecutiveLowBacklogTicks = 0;
        latencyExpansionActive = false;
        Interlocked.Exchange(ref underruns, 0);
        Interlocked.Exchange(ref warmupCycles, 0);
        Interlocked.Exchange(ref lastWarmupDurationTicks, 0);
        Interlocked.Exchange(ref currentWarmupRepeatTicks, 0);
        Interlocked.Exchange(ref lastWarmupRepeatTicks, 0);
        Interlocked.Exchange(ref lowWatermarkHits, 0);
        Interlocked.Exchange(ref highWatermarkHits, 0);
        Interlocked.Exchange(ref latencyResyncDrops, 0);
        Interlocked.Exchange(ref latencyExpansionSessions, 0);
        Interlocked.Exchange(ref latencyExpansionTicks, 0);
        Interlocked.Exchange(ref latencyExpansionFramesServed, 0);
        Interlocked.Exchange(ref captureGatePauses, 0);
        Interlocked.Exchange(ref captureGateResumes, 0);
        Interlocked.Exchange(ref lastPacingOffsetTicks, 0);

        ringBuffer?.Clear();
        lastSentFrame?.Dispose();
        lastSentFrame = null;
        captureGateActive = false;
        consecutivePositiveLatencyTicks = 0;
        invalidationScheduler?.Resume();
    }

    private double ComputeLastWarmupMilliseconds()
    {
        var ticks = Interlocked.Read(ref lastWarmupDurationTicks);
        return ticks <= 0 ? 0 : ticks / (double)TimeSpan.TicksPerMillisecond;
    }

    internal bool BufferPrimed => !BufferingEnabled || bufferPrimed;

    internal long BufferUnderruns => Interlocked.Read(ref underruns);

    internal TimeSpan LastWarmupDuration => TimeSpan.FromTicks(Interlocked.Read(ref lastWarmupDurationTicks));

    internal long LastWarmupRepeats => Interlocked.Read(ref lastWarmupRepeatTicks);

    internal bool LatencyExpansionActive => allowLatencyExpansion && Volatile.Read(ref latencyExpansionActive);

    internal long LatencyExpansionSessions => Interlocked.Read(ref latencyExpansionSessions);

    internal long LatencyExpansionTicks => Interlocked.Read(ref latencyExpansionTicks);

    internal long LatencyExpansionFramesServed => Interlocked.Read(ref latencyExpansionFramesServed);

    internal bool CaptureGateActive => captureBackpressureEnabled && captureGateActive;

    internal long PendingInvalidations => Volatile.Read(ref pendingInvalidations);

    internal long SpuriousCaptureCount => Interlocked.Read(ref spuriousCaptureCount);

    private (int numerator, int denominator) ResolveFrameRate(DateTime timestamp)
    {
        var measured = timeAverager.AddTimestamp(timestamp);
        if (!measured.HasValue)
        {
            return (configuredFrameRate.Numerator, configuredFrameRate.Denominator);
        }

        try
        {
            var measuredRate = FrameRate.FromDouble(measured.Value);
            return (measuredRate.Numerator, measuredRate.Denominator);
        }
        catch
        {
            return (configuredFrameRate.Numerator, configuredFrameRate.Denominator);
        }
    }

    private sealed class CadenceTracker
    {
        private readonly double targetIntervalTicks;
        private readonly object gate = new();
        private long originTimestamp;
        private bool hasOrigin;
        private long lastTimestamp;
        private double lastRelativeTicks;
        private long intervalSamples;
        private double sumSquaredIntervalErrorTicks;
        private double maxIntervalErrorTicks;
        private double minIntervalErrorTicks;

        public CadenceTracker(TimeSpan targetInterval)
        {
            targetIntervalTicks = Math.Max(1, targetInterval.Ticks);
            Reset();
        }

        public void Reset()
        {
            lock (gate)
            {
                hasOrigin = false;
                originTimestamp = 0;
                lastTimestamp = 0;
                lastRelativeTicks = 0;
                intervalSamples = 0;
                sumSquaredIntervalErrorTicks = 0;
                maxIntervalErrorTicks = 0;
                minIntervalErrorTicks = 0;
            }
        }

        public void Record(long timestamp)
        {
            lock (gate)
            {
                if (!hasOrigin)
                {
                    originTimestamp = timestamp;
                    lastTimestamp = timestamp;
                    lastRelativeTicks = 0;
                    intervalSamples = 0;
                    sumSquaredIntervalErrorTicks = 0;
                    maxIntervalErrorTicks = 0;
                    minIntervalErrorTicks = 0;
                    hasOrigin = true;
                    return;
                }

                var relativeTicks = (timestamp - originTimestamp) * StopwatchTicksToTimeSpanTicks;
                var intervalTicks = (timestamp - lastTimestamp) * StopwatchTicksToTimeSpanTicks;
                var intervalError = intervalTicks - targetIntervalTicks;

                if (intervalSamples == 0)
                {
                    maxIntervalErrorTicks = intervalError;
                    minIntervalErrorTicks = intervalError;
                }
                else
                {
                    if (intervalError > maxIntervalErrorTicks)
                    {
                        maxIntervalErrorTicks = intervalError;
                    }

                    if (intervalError < minIntervalErrorTicks)
                    {
                        minIntervalErrorTicks = intervalError;
                    }
                }

                intervalSamples++;
                sumSquaredIntervalErrorTicks += intervalError * intervalError;
                lastTimestamp = timestamp;
                lastRelativeTicks = relativeTicks;
            }
        }

        public double GetDriftFrames()
        {
            lock (gate)
            {
                if (!hasOrigin || intervalSamples == 0 || targetIntervalTicks == 0)
                {
                    return 0;
                }

                var expectedTicks = intervalSamples * targetIntervalTicks;
                var driftTicks = lastRelativeTicks - expectedTicks;
                if (double.IsNaN(driftTicks))
                {
                    return 0;
                }

                return driftTicks / targetIntervalTicks;
            }
        }

        public CadenceSnapshot GetSnapshot()
        {
            lock (gate)
            {
                if (!hasOrigin)
                {
                    return CadenceSnapshot.Empty;
                }

                if (intervalSamples == 0)
                {
                    return new CadenceSnapshot(0, 0, 0, 0, 0, targetIntervalTicks);
                }

                var expectedTicks = intervalSamples * targetIntervalTicks;
                var driftTicks = lastRelativeTicks - expectedTicks;
                var rms = Math.Sqrt(sumSquaredIntervalErrorTicks / intervalSamples);
                if (double.IsNaN(rms))
                {
                    rms = 0;
                }

                return new CadenceSnapshot(intervalSamples, rms, maxIntervalErrorTicks, minIntervalErrorTicks, driftTicks, targetIntervalTicks);
            }
        }
    }

    private readonly struct CadenceSnapshot
    {
        public static CadenceSnapshot Empty { get; } = new CadenceSnapshot(0, 0, 0, 0, 0, 1);

        public CadenceSnapshot(long intervalSamples, double rmsTicks, double maxTicks, double minTicks, double driftTicks, double targetIntervalTicks)
        {
            IntervalSamples = intervalSamples;
            IntervalRmsTicks = rmsTicks;
            MaxIntervalErrorTicks = maxTicks;
            MinIntervalErrorTicks = minTicks;
            DriftTicks = driftTicks;
            TargetIntervalTicks = targetIntervalTicks;
        }

        public long IntervalSamples { get; }

        public double IntervalRmsTicks { get; }

        public double MaxIntervalErrorTicks { get; }

        public double MinIntervalErrorTicks { get; }

        public double DriftTicks { get; }

        public double TargetIntervalTicks { get; }

        public double IntervalRmsMilliseconds => IntervalSamples == 0 ? 0 : IntervalRmsTicks / TimeSpan.TicksPerMillisecond;

        public double PeakIntervalErrorMilliseconds => IntervalSamples == 0
            ? 0
            : Math.Max(Math.Abs(MaxIntervalErrorTicks), Math.Abs(MinIntervalErrorTicks)) / TimeSpan.TicksPerMillisecond;

        public double DriftMilliseconds => IntervalSamples == 0 ? 0 : DriftTicks / TimeSpan.TicksPerMillisecond;

        public double DriftFrames => IntervalSamples == 0 || TargetIntervalTicks == 0 ? 0 : DriftTicks / TargetIntervalTicks;
    }

    private static NDIlib.video_frame_v2_t CreateVideoFrame(NdiVideoFrame frame, int numerator, int denominator)
    {
        return new NDIlib.video_frame_v2_t
        {
            FourCC = NDIlib.FourCC_type_e.FourCC_type_BGRA,
            frame_rate_N = numerator,
            frame_rate_D = denominator,
            frame_format_type = NDIlib.frame_format_type_e.frame_format_type_progressive,
            line_stride_in_bytes = frame.Stride,
            picture_aspect_ratio = frame.Width / (float)frame.Height,
            p_data = frame.Buffer,
            timecode = NDIlib.send_timecode_synthesize,
            xres = frame.Width,
            yres = frame.Height,
        };
    }

    private static NDIlib.video_frame_v2_t CreateVideoFrame(CapturedFrame frame, int numerator, int denominator)
    {
        return new NDIlib.video_frame_v2_t
        {
            FourCC = NDIlib.FourCC_type_e.FourCC_type_BGRA,
            frame_rate_N = numerator,
            frame_rate_D = denominator,
            frame_format_type = NDIlib.frame_format_type_e.frame_format_type_progressive,
            line_stride_in_bytes = frame.Stride,
            picture_aspect_ratio = frame.Width / (float)frame.Height,
            p_data = frame.Buffer,
            timecode = NDIlib.send_timecode_synthesize,
            xres = frame.Width,
            yres = frame.Height,
        };
    }

    private void EmitTelemetryIfNeeded([CallerMemberName] string? caller = null)
    {
        if (DateTime.UtcNow - lastTelemetry < options.TelemetryInterval)
        {
            return;
        }

        lastTelemetry = DateTime.UtcNow;

        var bufferStats = BufferingEnabled && ringBuffer is not null
            ? $", primed={bufferPrimed}, buffered={ringBuffer.Count}, droppedOverflow={ringBuffer.DroppedFromOverflow}, droppedStale={ringBuffer.DroppedAsStale}, underruns={Interlocked.Read(ref underruns)}, warmups={Interlocked.Read(ref warmupCycles)}, lastWarmupMs={ComputeLastWarmupMilliseconds():F1}, lastWarmupRepeats={Interlocked.Read(ref lastWarmupRepeatTicks)}, lowWaterHits={Interlocked.Read(ref lowWatermarkHits)}, highWaterHits={Interlocked.Read(ref highWatermarkHits)}, resyncDrops={Interlocked.Read(ref latencyResyncDrops)}, latencyError={Volatile.Read(ref latencyError):F2}"
            : string.Empty;
        if (BufferingEnabled && ringBuffer is not null)
        {
            bufferStats += $", latencyExpansionSessions={Interlocked.Read(ref latencyExpansionSessions)}, latencyExpansionTicks={Interlocked.Read(ref latencyExpansionTicks)}, latencyExpansionFrames={Interlocked.Read(ref latencyExpansionFramesServed)}";
        }
        if (captureBackpressureEnabled)
        {
            bufferStats += $", captureGateActive={captureGateActive}, captureGatePauses={Interlocked.Read(ref captureGatePauses)}, captureGateResumes={Interlocked.Read(ref captureGateResumes)}";
        }
        if (BufferingEnabled && pacedInvalidationEnabled)
        {
            var scheduler = invalidationScheduler;
            if (scheduler is not null)
            {
                var pausedSnapshot = scheduler.IsPaused;
                var offsetTicks = Interlocked.Read(ref lastPacingOffsetTicks);
                var offsetMs = offsetTicks / (double)TimeSpan.TicksPerMillisecond;
                bufferStats += System.FormattableString.Invariant(
                    $", pacedInvalidation=true, pacedPaused={pausedSnapshot}, pacedOffsetMs={offsetMs:F4}, cadenceAdaptation={pumpCadenceAdaptationEnabled}");
            }
        }

        var pacingStats = string.Empty;
        if (directPacedInvalidationEnabled || pacedInvalidationEnabled)
        {
            var pending = Volatile.Read(ref pendingInvalidations);
            var spurious = Interlocked.Read(ref spuriousCaptureCount);
            var expired = Interlocked.Read(ref expiredInvalidationTickets);
            pacingStats = $", pendingInvalidations={pending}, spuriousCaptures={spurious}, expiredInvalidationTickets={expired}";
        }

        var cadenceStats = string.Empty;
        if (cadenceTelemetryEnabled)
        {
            var captureSnapshot = captureCadenceTracker.GetSnapshot();
            var outputSnapshot = outputCadenceTracker.GetSnapshot();
            var alignmentDelta = Interlocked.CompareExchange(ref cadenceAlignmentDeltaFrames, 0d, 0d);
            if (!double.IsFinite(alignmentDelta))
            {
                alignmentDelta = 0;
            }

            cadenceStats = System.FormattableString.Invariant(
                $", captureJitterRmsMs={captureSnapshot.IntervalRmsMilliseconds:F4}, captureJitterPkMs={captureSnapshot.PeakIntervalErrorMilliseconds:F4}, captureDriftMs={captureSnapshot.DriftMilliseconds:F4}, captureIntervals={captureSnapshot.IntervalSamples}, outputJitterRmsMs={outputSnapshot.IntervalRmsMilliseconds:F4}, outputJitterPkMs={outputSnapshot.PeakIntervalErrorMilliseconds:F4}, outputDriftMs={outputSnapshot.DriftMilliseconds:F4}, outputIntervals={outputSnapshot.IntervalSamples}, driftDeltaFrames={alignmentDelta:F4}");
        }

        logger.Information(
            "NDI video pipeline stats: captured={Captured}, sent={Sent}, repeated={Repeated}{BufferStats}{PacingStats}{CadenceStats} (caller={Caller})",
            Interlocked.Read(ref capturedFrames),
            Interlocked.Read(ref sentFrames),
            Interlocked.Read(ref repeatedFrames),
            bufferStats,
            pacingStats,
            cadenceStats,
            caller);
    }

    /// <summary>
    /// Releases the resources used by the video pipeline.
    /// </summary>
    public void Dispose()
    {
        Stop();
        ringBuffer?.Clear();
        lastSentFrame?.Dispose();
        cancellation.Dispose();
    }
}
