using NewTek;
using NewTek.NDI;
using Serilog;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Tractus.HtmlToNdi.Video;

internal sealed class NdiVideoPipeline : IDisposable
{
    private readonly INdiVideoSender sender;
    private readonly FrameRate configuredFrameRate;
    private readonly NdiVideoPipelineOptions options;
    private readonly FrameTimeAverager timeAverager = new();
    private readonly CancellationTokenSource cancellation = new();
    private readonly FrameRingBuffer<NdiVideoFrame>? ringBuffer;
    private readonly ILogger logger;

    // --- Paced buffering state ---
    private readonly int targetDepth;
    private readonly double lowWatermark;
    private readonly int highWatermark;
    private bool isWarmingUp = true;
    private double latencyError;
    private DateTime warmupStart;
    private long underrunEvents;
    private int repeatedFramesInCurrentWarmup;
    private long warmupCycles;
    private long lastWarmupDurationTicks;

    // --- Latency expansion state ---
    private bool isLatencyExpansionActive;
    private long latencyExpansionTicks;
    private long framesSentDuringExpansion;

    private Task? pacingTask;
    private NdiVideoFrame? lastSentFrame;
    private long capturedFrames;
    private long sentFrames;
    private long repeatedFrames;
    private DateTime lastTelemetry = DateTime.UtcNow;

    public NdiVideoPipeline(INdiVideoSender sender, FrameRate frameRate, NdiVideoPipelineOptions options, ILogger logger)
    {
        this.sender = sender ?? throw new ArgumentNullException(nameof(sender));
        configuredFrameRate = frameRate;
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.logger = logger;

        if (options.EnableBuffering)
        {
            targetDepth = Math.Max(1, options.BufferDepth);
            lowWatermark = targetDepth - 0.5;
            highWatermark = targetDepth + 1;
            ringBuffer = new FrameRingBuffer<NdiVideoFrame>(highWatermark);
            warmupStart = DateTime.UtcNow;
        }
    }

    private bool TrySendBufferedFrame()
    {
        if (ringBuffer is null)
            return false;

        var backlog = ringBuffer.Count;
        latencyError += backlog - targetDepth;

        if (isWarmingUp)
        {
            // If we are actively expanding latency, we are allowed to send frames.
            if (isLatencyExpansionActive)
            {
                Interlocked.Increment(ref latencyExpansionTicks);
            }

            // Check if we can exit the warm-up/expansion state.
            if (backlog >= targetDepth && latencyError >= 0)
            {
                var duration = DateTime.UtcNow - warmupStart;
                Interlocked.Increment(ref warmupCycles);
                Interlocked.Exchange(ref lastWarmupDurationTicks, duration.Ticks);

                if (isLatencyExpansionActive)
                {
                    logger.Information(
                        "Paced buffer latency expansion complete. Resuming normal pacing. (duration={DurationMs}ms, framesSentInMode={FramesSent})",
                        duration.TotalMilliseconds,
                        Interlocked.Read(ref framesSentDuringExpansion));
                    isLatencyExpansionActive = false;
                }
                else
                {
                    logger.Information(
                        "Paced buffer priming complete. Resuming normal pacing. (duration={DurationMs}ms, repeatedFrames={RepeatedFrames})",
                        duration.TotalMilliseconds,
                        repeatedFramesInCurrentWarmup);
                }

                isWarmingUp = false;
                repeatedFramesInCurrentWarmup = 0;
            }
            else if (!isLatencyExpansionActive)
            {
                // Not expanding latency, so we must be in a hard warm-up. Repeat the last frame.
                return false;
            }
            // If we are here, it means isWarmingUp=true and isLatencyExpansionActive=true, but buffer isn't healthy.
            // We fall through to attempt sending a frame from the remaining buffer.
        }

        // Check for underrun condition.
        if (backlog < lowWatermark && !isWarmingUp)
        {
            logger.Warning(
                "Paced buffer underrun detected (backlog={Backlog}, target={TargetDepth}).",
                backlog, targetDepth);

            isWarmingUp = true;
            warmupStart = DateTime.UtcNow;
            Interlocked.Increment(ref underrunEvents);

            // If expansion is enabled and we have frames, enter that mode.
            if (options.EnableLatencyExpansion && backlog > 0)
            {
                isLatencyExpansionActive = true;
                logger.Information("Entering latency expansion mode to gracefully handle underrun while preserving {Backlog} buffered frames.", backlog);
                // Fall through to send remaining frames.
            }
            else
            {
                // Otherwise, enter a hard warm-up: drain and repeat.
                logger.Information("Repeating last frame and re-priming.");
                latencyError = 0;
                ringBuffer.DrainToLatestAndKeep();
                return false;
            }
        }

        // High watermark check: if we are accumulating latency, drop a frame.
        // When expanding latency, we suppress this to allow the buffer to recover.
        while (latencyError > 1.0 && ringBuffer.Count > targetDepth && !isLatencyExpansionActive)
        {
            if (ringBuffer.TryDequeue(out var droppedFrame))
            {
                droppedFrame.Dispose();
                latencyError--;
                logger.Verbose("Paced buffer latency integrator triggered frame drop to reduce delay.");
            }
            else
            {
                break;
            }
        }

        // Try to send a frame. This will run in normal pacing or during latency expansion.
        if (ringBuffer.TryDequeue(out var frame))
        {
            if (isLatencyExpansionActive)
            {
                Interlocked.Increment(ref framesSentDuringExpansion);
            }
            SendBufferedFrame(frame);
            return true;
        }

        // Dequeue failed, meaning the buffer is empty. We must enter a hard warm-up and repeat.
        if (!isWarmingUp || isLatencyExpansionActive)
        {
            logger.Warning("Buffer is empty; entering hard warm-up to repeat frames.");
            isWarmingUp = true;
            warmupStart = DateTime.UtcNow;
            latencyError = 0;
            isLatencyExpansionActive = false; // Can't expand if empty
            Interlocked.Increment(ref underrunEvents);
        }

        return false;
    }

    public bool BufferingEnabled => options.EnableBuffering;

    public FrameRate FrameRate => configuredFrameRate;

    internal bool BufferPrimed => !isWarmingUp;

    public void Start()
    {
        if (!BufferingEnabled || pacingTask != null)
        {
            return;
        }

        ResetBufferingState();
        pacingTask = Task.Run(async () => await RunPacedLoopAsync(cancellation.Token));
    }

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
        isWarmingUp = true;
    }

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

        NdiVideoFrame? dropped = null;
        var copy = NdiVideoFrame.CopyFrom(frame);
        ringBuffer.Enqueue(copy, out dropped);
        dropped?.Dispose();
        EmitTelemetryIfNeeded();
    }

    private async Task RunPacedLoopAsync(CancellationToken token)
    {
        var timer = new PeriodicTimer(FrameRate.FrameDuration);
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

    private void RepeatLastFrame()
    {
        if (lastSentFrame is null)
        {
            return;
        }

        if (isWarmingUp)
        {
            repeatedFramesInCurrentWarmup++;
        }

        var ndiFrame = CreateVideoFrame(lastSentFrame, configuredFrameRate.Numerator, configuredFrameRate.Denominator);
        sender.Send(ref ndiFrame);
        Interlocked.Increment(ref repeatedFrames);
        EmitTelemetryIfNeeded();
    }

    private void ResetBufferingState()
    {
        isWarmingUp = true;
        latencyError = 0;
        warmupStart = DateTime.UtcNow;
        repeatedFramesInCurrentWarmup = 0;
        Interlocked.Exchange(ref underrunEvents, 0);
        Interlocked.Exchange(ref warmupCycles, 0);
        Interlocked.Exchange(ref lastWarmupDurationTicks, 0);

        isLatencyExpansionActive = false;
        Interlocked.Exchange(ref latencyExpansionTicks, 0);
        Interlocked.Exchange(ref framesSentDuringExpansion, 0);

        ringBuffer?.Clear();
        lastSentFrame?.Dispose();
        lastSentFrame = null;
    }


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

    private double ComputeLastWarmupMilliseconds()
    {
        var ticks = Interlocked.Read(ref lastWarmupDurationTicks);
        return ticks <= 0 ? 0 : ticks / (double)TimeSpan.TicksPerMillisecond;
    }

    private void EmitTelemetryIfNeeded([CallerMemberName] string? caller = null)
    {
        if (DateTime.UtcNow - lastTelemetry < options.TelemetryInterval)
        {
            return;
        }

        lastTelemetry = DateTime.UtcNow;

        var bufferStats = string.Empty;
        if (BufferingEnabled && ringBuffer is not null)
        {
            var expansionStats = isLatencyExpansionActive
                ? $", expansionTicks={Interlocked.Read(ref latencyExpansionTicks)}, expansionFrames={Interlocked.Read(ref framesSentDuringExpansion)}"
                : string.Empty;

            bufferStats =
                $", warmingUp={isWarmingUp}, buffered={ringBuffer.Count}, latencyError={latencyError:F2}, underruns={Interlocked.Read(ref underrunEvents)}, warmups={Interlocked.Read(ref warmupCycles)}, lastWarmupMs={ComputeLastWarmupMilliseconds():F1}, droppedOverflow={ringBuffer.DroppedFromOverflow}, droppedStale={ringBuffer.DroppedAsStale}{expansionStats}";
        }

        logger.Information(
            "NDI video pipeline stats: captured={Captured}, sent={Sent}, repeated={Repeated}{BufferStats} (caller={Caller})",
            Interlocked.Read(ref capturedFrames),
            Interlocked.Read(ref sentFrames),
            Interlocked.Read(ref repeatedFrames),
            bufferStats,
            caller);
    }

    public void Dispose()
    {
        Stop();
        ringBuffer?.Clear();
        lastSentFrame?.Dispose();
        cancellation.Dispose();
    }
}
