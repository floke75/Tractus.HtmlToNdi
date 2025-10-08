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
    private bool bufferPrimed;
    private bool warmup = true;
    private double latencyError;
    private DateTime warmupStarted;
    private long underruns;
    private long warmupCycles;
    private long lastWarmupDurationTicks;
    private long warmupRepeatTicks;
    private long lastWarmupRepeatTicks;
    private long lowWatermarkCrossings;
    private long highWatermarkDrops;

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
            lowWatermark = Math.Max(0, targetDepth - 0.5);
            highWatermark = targetDepth + 1;
            ringBuffer = new FrameRingBuffer<NdiVideoFrame>(targetDepth + 1);
            warmupStarted = DateTime.UtcNow;
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

                ProcessBufferedTick();
            }
        }
        finally
        {
            timer.Dispose();
        }
    }

    private void ProcessBufferedTick()
    {
        if (ringBuffer is null)
        {
            if (lastSentFrame is not null)
            {
                RepeatLastFrame(isWarmupTick: false);
            }

            return;
        }

        if (warmup)
        {
            HandleWarmupTick();
            return;
        }

        HandlePrimedTick();
    }

    private void HandleWarmupTick()
    {
        if (ringBuffer is null)
        {
            return;
        }

        var backlog = ringBuffer.Count;
        latencyError += backlog - targetDepth;

        if (lastSentFrame is not null)
        {
            RepeatLastFrame(isWarmupTick: true);
        }

        if (backlog >= targetDepth && latencyError >= 0)
        {
            EnterPrimed();
        }
    }

    private void HandlePrimedTick()
    {
        if (ringBuffer is null)
        {
            return;
        }

        var backlog = ringBuffer.Count;

        if (backlog <= lowWatermark)
        {
            EnterWarmup(lowWatermarkTriggered: true);
            HandleWarmupTick();
            return;
        }

        if (!ringBuffer.TryDequeue(out var frame) || frame is null)
        {
            EnterWarmup(lowWatermarkTriggered: false);
            HandleWarmupTick();
            return;
        }

        SendBufferedFrame(frame);

        latencyError += backlog - targetDepth;

        TrimIfAhead();
    }

    private void TrimIfAhead()
    {
        if (ringBuffer is null)
        {
            return;
        }

        while (latencyError > 1 && ringBuffer.TryDequeueAsStale(out var drop) && drop is not null)
        {
            drop.Dispose();
            latencyError -= 1;
            Interlocked.Increment(ref highWatermarkDrops);
        }

        while (ringBuffer.Count > highWatermark && ringBuffer.TryDequeueAsStale(out var overflow) && overflow is not null)
        {
            overflow.Dispose();
            latencyError -= 1;
            Interlocked.Increment(ref highWatermarkDrops);
        }

        if (latencyError < -targetDepth * 4)
        {
            latencyError = -targetDepth * 4;
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

    private void RepeatLastFrame(bool isWarmupTick)
    {
        if (lastSentFrame is null)
        {
            return;
        }

        var ndiFrame = CreateVideoFrame(lastSentFrame, configuredFrameRate.Numerator, configuredFrameRate.Denominator);
        sender.Send(ref ndiFrame);
        Interlocked.Increment(ref repeatedFrames);

        if (isWarmupTick)
        {
            Interlocked.Increment(ref warmupRepeatTicks);
        }

        EmitTelemetryIfNeeded();
    }

    private void EnterPrimed()
    {
        bufferPrimed = true;
        warmup = false;

        var now = DateTime.UtcNow;
        var duration = now - warmupStarted;
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        warmupStarted = now;
        Interlocked.Increment(ref warmupCycles);
        Interlocked.Exchange(ref lastWarmupDurationTicks, duration.Ticks);
        Interlocked.Exchange(ref lastWarmupRepeatTicks, Interlocked.Exchange(ref warmupRepeatTicks, 0));
    }

    private void EnterWarmup(bool lowWatermarkTriggered)
    {
        if (lastSentFrame is not null)
        {
            Interlocked.Increment(ref underruns);
        }

        bufferPrimed = false;
        warmup = true;
        warmupRepeatTicks = 0;
        warmupStarted = DateTime.UtcNow;

        if (lowWatermarkTriggered)
        {
            Interlocked.Increment(ref lowWatermarkCrossings);
        }

        ringBuffer?.TrimToSingleLatest();
    }

    private void ResetBufferingState()
    {
        bufferPrimed = false;
        warmup = true;
        latencyError = 0;
        warmupStarted = DateTime.UtcNow;
        warmupRepeatTicks = 0;
        Interlocked.Exchange(ref underruns, 0);
        Interlocked.Exchange(ref warmupCycles, 0);
        Interlocked.Exchange(ref lastWarmupDurationTicks, 0);
        Interlocked.Exchange(ref lastWarmupRepeatTicks, 0);
        Interlocked.Exchange(ref lowWatermarkCrossings, 0);
        Interlocked.Exchange(ref highWatermarkDrops, 0);
    }

    private double ComputeLastWarmupMilliseconds()
    {
        var ticks = Interlocked.Read(ref lastWarmupDurationTicks);
        return ticks <= 0 ? 0 : ticks / (double)TimeSpan.TicksPerMillisecond;
    }

    internal bool BufferPrimed => bufferPrimed;

    internal long BufferUnderruns => Interlocked.Read(ref underruns);

    internal TimeSpan LastWarmupDuration => TimeSpan.FromTicks(Interlocked.Read(ref lastWarmupDurationTicks));

    internal long LastWarmupRepeats => Interlocked.Read(ref lastWarmupRepeatTicks);

    internal long LowWatermarkHits => Interlocked.Read(ref lowWatermarkCrossings);

    internal long HighWatermarkDropCount => Interlocked.Read(ref highWatermarkDrops);

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
            ? $", primed={bufferPrimed}, buffered={ringBuffer.Count}, droppedOverflow={ringBuffer.DroppedFromOverflow}, droppedStale={ringBuffer.DroppedAsStale}, underruns={Interlocked.Read(ref underruns)}, warmups={Interlocked.Read(ref warmupCycles)}, lastWarmupMs={ComputeLastWarmupMilliseconds():F1}, lastWarmupRepeats={Interlocked.Read(ref lastWarmupRepeatTicks)}, lowWatermarkHits={Interlocked.Read(ref lowWatermarkCrossings)}, highWatermarkDrops={Interlocked.Read(ref highWatermarkDrops)}, latencyError={Volatile.Read(ref latencyError):F2}"
            : string.Empty;

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
