using NewTek;
using NewTek.NDI;
using Serilog;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Tractus.HtmlToNdi.Video;

/// <summary>
/// Manages the video pipeline, including buffering and sending frames to NDI.
/// </summary>
internal sealed class NdiVideoPipeline : IDisposable
{
    /// <summary>
    /// The underlying NDI video sender.
    /// </summary>
    private readonly INdiVideoSender sender;

    /// <summary>
    /// The configured frame rate for the pipeline.
    /// </summary>
    private readonly FrameRate configuredFrameRate;

    /// <summary>
    /// The options for configuring the pipeline's behavior.
    /// </summary>
    private readonly NdiVideoPipelineOptions options;

    /// <summary>
    /// A helper for calculating the average frame time.
    /// </summary>
    private readonly FrameTimeAverager timeAverager = new();

    /// <summary>
    /// A cancellation token source for stopping the pipeline.
    /// </summary>
    private readonly CancellationTokenSource cancellation = new();

    /// <summary>
    /// The ring buffer for storing video frames when buffering is enabled.
    /// </summary>
    private readonly FrameRingBuffer<NdiVideoFrame>? ringBuffer;

    /// <summary>
    /// The logger for recording events.
    /// </summary>
    private readonly ILogger logger;

    /// <summary>
    /// The target depth of the frame buffer.
    /// </summary>
    private readonly int targetDepth;

    /// <summary>
    /// The low watermark for the frame buffer, below which the pipeline may enter a warmup state.
    /// </summary>
    private readonly double lowWatermark;

    /// <summary>
    /// The high watermark for the frame buffer, above which the pipeline may drop frames.
    /// </summary>
    private readonly double highWatermark;

    /// <summary>
    /// A flag indicating whether latency expansion is allowed.
    /// </summary>
    private readonly bool allowLatencyExpansion;

    /// <summary>
    /// A flag indicating whether the buffer has been primed with enough frames.
    /// </summary>
    private bool bufferPrimed;

    /// <summary>
    /// A flag indicating whether the pipeline is currently in a warmup state.
    /// </summary>
    private bool isWarmingUp = true;

    /// <summary>
    /// A flag indicating whether the buffer has been primed at least once.
    /// </summary>
    private bool hasPrimedOnce;

    /// <summary>
    /// An accumulator for tracking latency errors.
    /// </summary>
    private double latencyError;

    /// <summary>
    /// The number of consecutive ticks with a low backlog.
    /// </summary>
    private int consecutiveLowBacklogTicks;

    /// <summary>
    /// The timestamp when the last warmup period started.
    /// </summary>
    private DateTime warmupStarted;

    /// <summary>
    /// The number of buffer underruns that have occurred.
    /// </summary>
    private long underruns;

    /// <summary>
    /// The number of warmup cycles that have occurred.
    /// </summary>
    private long warmupCycles;

    /// <summary>
    /// The duration of the last warmup period in ticks.
    /// </summary>
    private long lastWarmupDurationTicks;

    /// <summary>
    /// The number of repeated frames during the current warmup period.
    /// </summary>
    private long currentWarmupRepeatTicks;

    /// <summary>
    /// The number of repeated frames during the last warmup period.
    /// </summary>
    private long lastWarmupRepeatTicks;

    /// <summary>
    /// The number of times the low watermark has been hit.
    /// </summary>
    private long lowWatermarkHits;

    /// <summary>
    /// The number of times the high watermark has been hit.
    /// </summary>
    private long highWatermarkHits;

    /// <summary>
    /// The number of frames dropped for latency resynchronization.
    /// </summary>
    private long latencyResyncDrops;

    /// <summary>
    /// A flag indicating whether latency expansion is currently active.
    /// </summary>
    private bool latencyExpansionActive;

    /// <summary>
    /// The number of latency expansion sessions that have occurred.
    /// </summary>
    private long latencyExpansionSessions;

    /// <summary>
    /// The number of ticks that have occurred during latency expansion.
    /// </summary>
    private long latencyExpansionTicks;

    /// <summary>
    /// The number of frames served during latency expansion.
    /// </summary>
    private long latencyExpansionFramesServed;

    /// <summary>
    /// The task for the paced loop that sends frames.
    /// </summary>
    private Task? pacingTask;

    /// <summary>
    /// The last frame that was sent.
    /// </summary>
    private NdiVideoFrame? lastSentFrame;

    /// <summary>
    /// The total number of frames captured.
    /// </summary>
    private long capturedFrames;

    /// <summary>
    /// The total number of frames sent.
    /// </summary>
    private long sentFrames;

    /// <summary>
    /// The total number of frames repeated.
    /// </summary>
    private long repeatedFrames;

    /// <summary>
    /// The timestamp of the last telemetry emission.
    /// </summary>
    private DateTime lastTelemetry = DateTime.UtcNow;

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
        lowWatermark = Math.Max(0, targetDepth - 0.5);
        highWatermark = targetDepth + 1;
        allowLatencyExpansion = options.AllowLatencyExpansion && options.EnableBuffering;

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

    /// <summary>
    /// Starts the video pipeline's paced sending loop if buffering is enabled.
    /// </summary>
    public void Start()
    {
        if (!BufferingEnabled || pacingTask != null)
        {
            return;
        }

        ResetBufferingState();
        pacingTask = Task.Run(async () => await RunPacedLoopAsync(cancellation.Token));
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
    }

    /// <summary>
    /// Handles a captured video frame.
    /// </summary>
    /// <param name="frame">The captured video frame.</param>
    public void HandleFrame(CapturedFrame frame)
    {
        Interlocked.Increment(ref capturedFrames);

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

    /// <summary>
    /// Runs the paced loop that sends frames at a regular interval.
    /// </summary>
    /// <param name="token">A cancellation token to stop the loop.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    private async Task RunPacedLoopAsync(CancellationToken token)
    {
        var interval = TimeSpan.FromSeconds(1 / FrameRate.Value);
        var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                if (token.IsCancellationRequested)
                {
                    break;
                }

                var sent = TrySendBufferedFrame();
                if (!sent && lastSentFrame is not null)
                {
                    RepeatLastFrame();
                }
            }
        }
        finally
        {
            timer.Dispose();
        }
    }

    /// <summary>
    /// Attempts to send a buffered frame, handling warmup, latency, and underrun conditions.
    /// </summary>
    /// <returns>True if a frame was sent; otherwise, false.</returns>
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

    /// <summary>
    /// Sends a captured frame directly to NDI without buffering.
    /// </summary>
    /// <param name="frame">The captured frame to send.</param>
    private void SendDirect(CapturedFrame frame)
    {
        if (frame.Buffer == IntPtr.Zero)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var (numerator, denominator) = ResolveFrameRate(now);

        var ndiFrame = CreateVideoFrame(frame, numerator, denominator);
        sender.Send(ref ndiFrame);
        Interlocked.Increment(ref sentFrames);
        EmitTelemetryIfNeeded();
    }

    /// <summary>
    /// Sends a buffered video frame to NDI.
    /// </summary>
    /// <param name="frame">The video frame to send.</param>
    private void SendBufferedFrame(NdiVideoFrame frame)
    {
        var (numerator, denominator) = ResolveFrameRate(frame.Timestamp);

        var ndiFrame = CreateVideoFrame(frame, numerator, denominator);
        sender.Send(ref ndiFrame);

        Interlocked.Increment(ref sentFrames);

        lastSentFrame?.Dispose();
        lastSentFrame = frame;

        EmitTelemetryIfNeeded();
    }

    /// <summary>
    /// Repeats the last sent frame, which is used to maintain a constant frame rate during underruns.
    /// </summary>
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
        EmitTelemetryIfNeeded();
    }

    /// <summary>
    /// Enters the warmup state, which is used to buffer frames before starting to send them.
    /// </summary>
    /// <param name="preserveBufferedFrames">
    /// A flag indicating whether to preserve the existing frames in the buffer.
    /// </param>
    private void EnterWarmup(bool preserveBufferedFrames = false)
    {
        if (!BufferingEnabled || ringBuffer is null)
        {
            return;
        }

        var preserving = preserveBufferedFrames && allowLatencyExpansion && ringBuffer.Count > 0;

        if (!isWarmingUp && hasPrimedOnce && lastSentFrame is not null)
        {
            Interlocked.Increment(ref underruns);
            logger.Warning(
                "NDI pacer underrun detected: buffered={Buffered}, latencyError={LatencyError:F2}, preservingBufferedFrames={Preserving}",
                ringBuffer.Count,
                latencyError,
                preserving);
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
    }

    /// <summary>
    /// Exits the warmup state and begins sending frames.
    /// </summary>
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
    }

    /// <summary>
    /// Resets the buffering state to its initial values.
    /// </summary>
    private void ResetBufferingState()
    {
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

        ringBuffer?.Clear();
        lastSentFrame?.Dispose();
        lastSentFrame = null;
    }

    /// <summary>
    /// Computes the duration of the last warmup period in milliseconds.
    /// </summary>
    /// <returns>The duration of the last warmup period in milliseconds.</returns>
    private double ComputeLastWarmupMilliseconds()
    {
        var ticks = Interlocked.Read(ref lastWarmupDurationTicks);
        return ticks <= 0 ? 0 : ticks / (double)TimeSpan.TicksPerMillisecond;
    }

    /// <summary>
    /// Gets a value indicating whether the buffer is primed.
    /// </summary>
    internal bool BufferPrimed => !BufferingEnabled || bufferPrimed;

    /// <summary>
    /// Gets the number of buffer underruns.
    /// </summary>
    internal long BufferUnderruns => Interlocked.Read(ref underruns);

    /// <summary>
    /// Gets the duration of the last warmup period.
    /// </summary>
    internal TimeSpan LastWarmupDuration => TimeSpan.FromTicks(Interlocked.Read(ref lastWarmupDurationTicks));

    /// <summary>
    /// Gets the number of repeated frames during the last warmup period.
    /// </summary>
    internal long LastWarmupRepeats => Interlocked.Read(ref lastWarmupRepeatTicks);

    /// <summary>
    /// Gets a value indicating whether latency expansion is active.
    /// </summary>
    internal bool LatencyExpansionActive => allowLatencyExpansion && Volatile.Read(ref latencyExpansionActive);

    /// <summary>
    /// Gets the number of latency expansion sessions.
    /// </summary>
    internal long LatencyExpansionSessions => Interlocked.Read(ref latencyExpansionSessions);

    /// <summary>
    /// Gets the number of ticks that have occurred during latency expansion.
    /// </summary>
    internal long LatencyExpansionTicks => Interlocked.Read(ref latencyExpansionTicks);

    /// <summary>
    /// Gets the number of frames served during latency expansion.
    /// </summary>
    internal long LatencyExpansionFramesServed => Interlocked.Read(ref latencyExpansionFramesServed);

    /// <summary>
    /// Resolves the frame rate, using the measured frame rate if available,
    /// otherwise falling back to the configured frame rate.
    /// </summary>
    /// <param name="timestamp">The timestamp of the frame.</param>
    /// <returns>A tuple containing the numerator and denominator of the frame rate.</returns>
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

    /// <summary>
    /// Creates an NDI video frame from an NdiVideoFrame.
    /// </summary>
    /// <param name="frame">The NdiVideoFrame to convert.</param>
    /// <param name="numerator">The numerator of the frame rate.</param>
    /// <param name="denominator">The denominator of the frame rate.</param>
    /// <returns>A new NDIlib.video_frame_v2_t instance.</returns>
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

    /// <summary>
    /// Creates an NDI video frame from a CapturedFrame.
    /// </summary>
    /// <param name="frame">The CapturedFrame to convert.</param>
    /// <param name="numerator">The numerator of the frame rate.</param>
    /// <param name="denominator">The denominator of the frame rate.</param>
    /// <returns>A new NDIlib.video_frame_v2_t instance.</returns>
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

    /// <summary>
    /// Emits telemetry if the telemetry interval has elapsed.
    /// </summary>
    /// <param name="caller">The name of the calling member.</param>
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

        logger.Information(
            "NDI video pipeline stats: captured={Captured}, sent={Sent}, repeated={Repeated}{BufferStats} (caller={Caller})",
            Interlocked.Read(ref capturedFrames),
            Interlocked.Read(ref sentFrames),
            Interlocked.Read(ref repeatedFrames),
            bufferStats,
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
