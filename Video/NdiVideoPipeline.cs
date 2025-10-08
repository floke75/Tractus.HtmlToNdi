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
    private long lastWarmupRepeatTicks;
    private long warmupRepeatTicks;
    private long lowWatermarkCrossings;
    private long highWatermarkCrossings;
    private long integratorDrops;

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
        lowWatermark = targetDepth - 0.5d;
        highWatermark = targetDepth + 1d;

        if (options.EnableBuffering)
        {
            ringBuffer = new FrameRingBuffer<NdiVideoFrame>(targetDepth + 1);
            warmupStarted = DateTime.UtcNow;
        }
    }

    private bool TrySendBufferedFrame()
    {
        if (ringBuffer is null)
        {
            return false;
        }

        var backlog = ringBuffer.Count;
        latencyError += backlog - targetDepth;

        if (warmup)
        {
            if (backlog >= targetDepth && latencyError >= 0)
            {
                ExitWarmup();
            }
            else
            {
                RecordWarmupRepeat();
                return false;
            }
        }

        if (backlog <= lowWatermark)
        {
            Interlocked.Increment(ref lowWatermarkCrossings);
            EnterWarmup();
            RecordWarmupRepeat();
            return false;
        }

        if (backlog > highWatermark)
        {
            Interlocked.Increment(ref highWatermarkCrossings);
        }

        if (ringBuffer.TryDequeue(out var frame) && frame is not null)
        {
            SendBufferedFrame(frame);
            TrimForLatency();
            return true;
        }

        EnterWarmup();
        RecordWarmupRepeat();
        return false;
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
        warmup = true;
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

        var ndiFrame = CreateVideoFrame(lastSentFrame, configuredFrameRate.Numerator, configuredFrameRate.Denominator);
        sender.Send(ref ndiFrame);
        Interlocked.Increment(ref repeatedFrames);
        EmitTelemetryIfNeeded();
    }

    private void EnterWarmup()
    {
        if (!BufferingEnabled || ringBuffer is null)
        {
            return;
        }

        if (warmup)
        {
            ringBuffer.DropAllButLatest();
            latencyError = Math.Min(latencyError, 0);
            return;
        }

        warmup = true;
        bufferPrimed = false;
        warmupStarted = DateTime.UtcNow;
        Interlocked.Exchange(ref warmupRepeatTicks, 0);
        Interlocked.Increment(ref warmupCycles);

        if (lastSentFrame is not null)
        {
            Interlocked.Increment(ref underruns);
        }

        ringBuffer.DropAllButLatest();
        latencyError = Math.Min(latencyError, 0);
    }

    private void ResetBufferingState()
    {
        bufferPrimed = false;
        warmup = true;
        warmupStarted = DateTime.UtcNow;
        latencyError = 0;
        Interlocked.Exchange(ref underruns, 0);
        Interlocked.Exchange(ref warmupCycles, 0);
        Interlocked.Exchange(ref lastWarmupDurationTicks, 0);
        Interlocked.Exchange(ref warmupRepeatTicks, 0);
        Interlocked.Exchange(ref lastWarmupRepeatTicks, 0);
        Interlocked.Exchange(ref lowWatermarkCrossings, 0);
        Interlocked.Exchange(ref highWatermarkCrossings, 0);
        Interlocked.Exchange(ref integratorDrops, 0);
    }

    private double ComputeLastWarmupMilliseconds()
    {
        var ticks = Interlocked.Read(ref lastWarmupDurationTicks);
        return ticks <= 0 ? 0 : ticks / (double)TimeSpan.TicksPerMillisecond;
    }

    private long ComputeLastWarmupRepeats()
    {
        return Interlocked.Read(ref lastWarmupRepeatTicks);
    }

    internal bool BufferPrimed => bufferPrimed;

    internal long BufferUnderruns => Interlocked.Read(ref underruns);

    internal TimeSpan LastWarmupDuration => TimeSpan.FromTicks(Interlocked.Read(ref lastWarmupDurationTicks));

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
            ? $", primed={bufferPrimed}, buffered={ringBuffer.Count}, droppedOverflow={ringBuffer.DroppedFromOverflow}, droppedStale={ringBuffer.DroppedAsStale}, underruns={Interlocked.Read(ref underruns)}, warmups={Interlocked.Read(ref warmupCycles)}, lastWarmupMs={ComputeLastWarmupMilliseconds():F1}, lastWarmupRepeats={ComputeLastWarmupRepeats()}, latencyError={Volatile.Read(ref latencyError):F2}, lowWaterHits={Interlocked.Read(ref lowWatermarkCrossings)}, highWaterHits={Interlocked.Read(ref highWatermarkCrossings)}, integratorDrops={Interlocked.Read(ref integratorDrops)}"
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

    private void ExitWarmup()
    {
        if (!warmup)
        {
            return;
        }

        warmup = false;
        bufferPrimed = true;

        var now = DateTime.UtcNow;
        var duration = now - warmupStarted;
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        Interlocked.Exchange(ref lastWarmupDurationTicks, duration.Ticks);

        var repeats = Interlocked.Exchange(ref warmupRepeatTicks, 0);
        Interlocked.Exchange(ref lastWarmupRepeatTicks, repeats);

        logger.Warning(
            "NDI video pipeline underrun recovery: durationMs={DurationMs:F1}, repeatTicks={RepeatTicks}, targetDepth={TargetDepth}, latencyError={LatencyError:F2}",
            duration.TotalMilliseconds,
            repeats,
            targetDepth,
            latencyError);
    }

    private void RecordWarmupRepeat()
    {
        if (warmup)
        {
            Interlocked.Increment(ref warmupRepeatTicks);
        }
    }

    private void TrimForLatency()
    {
        if (ringBuffer is null)
        {
            return;
        }

        while (latencyError > 1d && ringBuffer.TryDiscardOldestAsStale())
        {
            latencyError -= 1d;
            Interlocked.Increment(ref integratorDrops);
        }
    }
}
