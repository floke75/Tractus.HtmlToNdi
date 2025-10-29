using NewTek;
using NewTek.NDI;
using Serilog;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Tractus.HtmlToNdi.Video;

/// <summary>
/// Manages the video pipeline, including buffering and sending frames to NDI.
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
    private readonly bool pacedInvalidationOption;
    private readonly bool captureBackpressureOption;
    private readonly bool pumpAlignmentOption;

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

    private volatile bool pacingResetRequested;

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
    private IChromiumInvalidationScheduler? invalidationScheduler;
    private int backpressureState;
    private long backpressurePauses;
    private long backpressureResumes;

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
        alignWithCaptureTimestamps = options.AlignWithCaptureTimestamps;
        cadenceTelemetryEnabled = options.EnableCadenceTelemetry;
        cadenceTrackingEnabled = alignWithCaptureTimestamps || cadenceTelemetryEnabled;
        captureCadenceTracker = new CadenceTracker(frameInterval);
        outputCadenceTracker = new CadenceTracker(frameInterval);
        pacedInvalidationOption = options.EnablePacedInvalidation;
        captureBackpressureOption = options.EnableCaptureBackpressure;
        pumpAlignmentOption = options.EnablePumpAlignment;

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

    internal NdiVideoPipelineOptions Options => options;

    /// <summary>
    /// Gets the configured frame rate.
    /// </summary>
    public FrameRate FrameRate => configuredFrameRate;

    /// <summary>
    /// Starts the video pipeline.
    /// </summary>
    public void Start()
    {
        if (!BufferingEnabled)
        {
            PrimeSchedulerIfNeeded();
            return;
        }

        if (pacingTask != null)
        {
            return;
        }

        ResetBufferingState();
        pacingResetRequested = true;
        pacingTask = Task.Run(async () => await RunPacedLoopAsync(cancellation.Token));
        PrimeSchedulerIfNeeded();
    }

    /// <summary>
    /// Stops the video pipeline.
    /// </summary>
    public void Stop()
    {
        cancellation.Cancel();
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
        ForceResumeBackpressure("pipeline stop");
    }

    /// <summary>
    /// Handles a captured video frame.
    /// </summary>
    /// <param name="frame">The captured video frame.</param>
    public void HandleFrame(CapturedFrame frame)
    {
        Interlocked.Increment(ref capturedFrames);
        if (cadenceTrackingEnabled)
        {
            captureCadenceTracker.Record(frame.MonotonicTimestamp);
        }

        if (!BufferingEnabled)
        {
            SendDirect(frame);
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
    }

    private async Task RunPacedLoopAsync(CancellationToken token)
    {
        var pacingClock = Stopwatch.StartNew();
        var pacingOrigin = pacingClock.Elapsed;
        long pacingSequence = 0;

        while (!token.IsCancellationRequested)
        {
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

            PublishSchedulerHints(ringBuffer?.Count ?? 0);
            pacingSequence = nextSequence;
        }
    }

    private TimeSpan CalculateNextDeadline(TimeSpan origin, long nextSequence)
    {
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

        var offset = targetDepth - backlog;
        var integral = Math.Clamp(Volatile.Read(ref latencyError) / Math.Max(1d, targetDepth), -2d, 2d);
        var normalized = Math.Clamp(offset / (double)targetDepth, -1.5d, 1.5d);
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

        return baseline + TimeSpan.FromTicks(adjustmentTicks);
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

        if (ringBuffer.TryDequeue(out var frame) && frame is not null)
        {
            SendBufferedFrame(frame);
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
        PublishSchedulerHints(0);
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
        pacingResetRequested = true;
        outputCadenceTracker.Reset();
        Interlocked.Exchange(ref cadenceAlignmentDeltaFrames, 0d);
        ForceResumeBackpressure("warmup entry");
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

        pacingResetRequested = true;
        ForceResumeBackpressure("warmup exit");
    }

    private void ResetBufferingState()
    {
        captureCadenceTracker.Reset();
        outputCadenceTracker.Reset();
        Interlocked.Exchange(ref cadenceAlignmentDeltaFrames, 0d);
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
        ForceResumeBackpressure("reset");
        Interlocked.Exchange(ref backpressureState, 0);
        Interlocked.Exchange(ref backpressurePauses, 0);
        Interlocked.Exchange(ref backpressureResumes, 0);

        ringBuffer?.Clear();
        lastSentFrame?.Dispose();
        lastSentFrame = null;
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

    internal void AttachInvalidationScheduler(IChromiumInvalidationScheduler? scheduler)
    {
        Volatile.Write(ref invalidationScheduler, scheduler);

        if (!pacedInvalidationOption)
        {
            return;
        }

        if (scheduler is null)
        {
            logger.Warning("Paced invalidation requested but no scheduler was supplied; Chromium will continue free-running.");
            return;
        }

        if (captureBackpressureOption && !pacedInvalidationOption)
        {
            logger.Warning("Capture backpressure requested without paced invalidation; enable paced invalidation to activate backpressure.");
        }

        if (pumpAlignmentOption && !pacedInvalidationOption)
        {
            logger.Warning("Pump alignment requested without paced invalidation; enable paced invalidation to share cadence telemetry with Chromium.");
        }

        if (captureBackpressureOption && !BufferingEnabled)
        {
            logger.Warning("Capture backpressure requested without buffering; invalidations will still be paced but backpressure cannot engage.");
        }

        // Request an initial invalidate in case the scheduler was attached after the pipeline
        // already requested pacing hints (for example, if Start() ran before Chromium finished
        // wiring up the pump). This mirrors the priming logic used when the pipeline starts but
        // avoids relying on call ordering for the first paced paint.
        PrimeSchedulerIfNeeded();
    }

    private void PrimeSchedulerIfNeeded()
    {
        var scheduler = Volatile.Read(ref invalidationScheduler);
        if (scheduler is null || !pacedInvalidationOption)
        {
            return;
        }

        scheduler.ResumeInvalidation();
        if (pumpAlignmentOption)
        {
            var delta = Interlocked.CompareExchange(ref cadenceAlignmentDeltaFrames, 0d, 0d);
            scheduler.UpdateAlignmentDelta(delta);
        }

        scheduler.RequestNextInvalidate();
    }

    private void PublishSchedulerHints(int backlog)
    {
        var scheduler = Volatile.Read(ref invalidationScheduler);
        if (scheduler is null || !pacedInvalidationOption)
        {
            return;
        }

        if (pumpAlignmentOption)
        {
            var delta = Interlocked.CompareExchange(ref cadenceAlignmentDeltaFrames, 0d, 0d);
            scheduler.UpdateAlignmentDelta(delta);
        }

        if (!captureBackpressureOption)
        {
            scheduler.RequestNextInvalidate();
            return;
        }

        if (UpdateBackpressureState(backlog))
        {
            scheduler.RequestNextInvalidate();
        }
    }

    private bool UpdateBackpressureState(int backlog)
    {
        var scheduler = Volatile.Read(ref invalidationScheduler);
        if (scheduler is null || !captureBackpressureOption)
        {
            return true;
        }

        var currentLatency = Volatile.Read(ref latencyError);
        var isActive = Volatile.Read(ref backpressureState) == 1;

        if (!isActive && (backlog >= highWatermark || currentLatency >= 1d))
        {
            if (Interlocked.CompareExchange(ref backpressureState, 1, 0) == 0)
            {
                Interlocked.Increment(ref backpressurePauses);
                logger.Warning("Pausing Chromium invalidation: backlog={Backlog}, latencyError={LatencyError:F2}", backlog, currentLatency);
                scheduler.PauseInvalidation();
            }

            return false;
        }

        if (isActive && (backlog <= targetDepth || currentLatency <= 0d))
        {
            if (Interlocked.Exchange(ref backpressureState, 0) == 1)
            {
                Interlocked.Increment(ref backpressureResumes);
                logger.Information("Resuming Chromium invalidation: backlog={Backlog}, latencyError={LatencyError:F2}", backlog, currentLatency);
                scheduler.ResumeInvalidation();
                return true;
            }
        }

        return !isActive;
    }

    private void ForceResumeBackpressure(string reason)
    {
        var scheduler = Volatile.Read(ref invalidationScheduler);
        if (scheduler is null)
        {
            return;
        }

        if (Interlocked.Exchange(ref backpressureState, 0) == 1)
        {
            Interlocked.Increment(ref backpressureResumes);
            logger.Information("Resuming Chromium invalidation after {Reason}", reason);
            scheduler.ResumeInvalidation();
            if (pacedInvalidationOption)
            {
                scheduler.RequestNextInvalidate();
            }
        }
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
        if (captureBackpressureOption)
        {
            bufferStats += $", backpressureActive={Volatile.Read(ref backpressureState) == 1}, backpressurePauses={Interlocked.Read(ref backpressurePauses)}, backpressureResumes={Interlocked.Read(ref backpressureResumes)}";
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
            "NDI video pipeline stats: captured={Captured}, sent={Sent}, repeated={Repeated}{BufferStats}{CadenceStats} (caller={Caller})",
            Interlocked.Read(ref capturedFrames),
            Interlocked.Read(ref sentFrames),
            Interlocked.Read(ref repeatedFrames),
            bufferStats,
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
