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
    private readonly int targetDepth;
    private readonly double lowWatermark;
    private readonly double highWatermark;
    private readonly bool allowLatencyExpansion;

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

    public bool BufferingEnabled => options.EnableBuffering;

    public FrameRate FrameRate => configuredFrameRate;

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
        bufferPrimed = false;
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

        var copy = NdiVideoFrame.CopyFrom(frame);
        ringBuffer.Enqueue(copy, out var dropped);
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
                if (backlog >= targetDepth && latencyError >= 0)
                {
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
            else if (backlog >= targetDepth && latencyError >= 0)
            {
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

        if (!latencyExpansionActive && latencyError > 1 && ringBuffer.Count > targetDepth)
        {
            while (latencyError > 1 && ringBuffer.Count > targetDepth && ringBuffer.TryDequeueAsStale(out var dropped))
            {
                if (dropped is not null)
                {
                    dropped.Dispose();
                }

                latencyError -= 1;
                Interlocked.Increment(ref latencyResyncDrops);
            }

            backlog = ringBuffer.Count;
            delta = backlog - targetDepth;
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
            Interlocked.Increment(ref currentWarmupRepeatTicks);
        }

        var ndiFrame = CreateVideoFrame(lastSentFrame, configuredFrameRate.Numerator, configuredFrameRate.Denominator);
        sender.Send(ref ndiFrame);
        Interlocked.Increment(ref repeatedFrames);
        EmitTelemetryIfNeeded();
    }

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

    internal long LatencyResyncDrops => Interlocked.Read(ref latencyResyncDrops);

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

    public void Dispose()
    {
        Stop();
        ringBuffer?.Clear();
        lastSentFrame?.Dispose();
        cancellation.Dispose();
    }
}
