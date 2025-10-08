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
    private readonly int targetDepthFrames;
    private readonly double targetDepth;
    private readonly double highWatermark;
    private readonly double lowWatermark;
    private int warmupState = 1;
    private double latencyError;
    private DateTime warmupStarted;
    private long underruns;
    private long warmupCycles;
    private long lastWarmupDurationTicks;
    private long warmupRepeatTicks;
    private long lastWarmupRepeatTicks;

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
            targetDepthFrames = Math.Max(1, options.BufferDepth);
            targetDepth = targetDepthFrames;
            highWatermark = targetDepth + 1;
            lowWatermark = Math.Max(0, targetDepth - 0.5);
            var capacity = targetDepthFrames + 2;
            ringBuffer = new FrameRingBuffer<NdiVideoFrame>(capacity);
            warmupStarted = DateTime.UtcNow;
        }
        else
        {
            targetDepthFrames = 0;
            targetDepth = 0;
            highWatermark = 0;
            lowWatermark = 0;
            warmupState = 0;
        }
    }

    private bool TrySendBufferedFrame()
    {
        if (ringBuffer is null)
        {
            return false;
        }

        var backlog = ringBuffer.Count;

        if (IsInWarmup)
        {
            if (backlog >= targetDepth && latencyError >= -0.001)
            {
                ExitWarmup();
                backlog = ringBuffer.Count;
            }
            else
            {
                UpdateWarmupState(backlog);
                return false;
            }
        }

        if (backlog <= lowWatermark)
        {
            EnterWarmup();
            UpdateWarmupState(ringBuffer.Count);
            return false;
        }

        if (!ringBuffer.TryDequeue(out var frame) || frame is null)
        {
            EnterWarmup();
            UpdateWarmupState(ringBuffer.Count);
            return false;
        }

        SendBufferedFrame(frame);

        backlog = ringBuffer.Count;
        latencyError += backlog - targetDepth;

        TrimBacklogIfNeeded();

        return true;
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
        if (BufferingEnabled)
        {
            Interlocked.Exchange(ref warmupState, 1);
        }
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

        var previous = Interlocked.Exchange(ref warmupState, 1);
        if (previous == 1)
        {
            return;
        }

        if (lastSentFrame is not null)
        {
            Interlocked.Increment(ref underruns);
        }

        warmupStarted = DateTime.UtcNow;
        ringBuffer.TrimToSingleLatest();
        Interlocked.Exchange(ref warmupRepeatTicks, 0);
        latencyError = Math.Min(latencyError, 0);
    }

    private void ResetBufferingState()
    {
        ringBuffer?.Clear();
        Interlocked.Exchange(ref warmupState, 1);
        latencyError = 0;
        warmupStarted = DateTime.UtcNow;
        Interlocked.Exchange(ref underruns, 0);
        Interlocked.Exchange(ref warmupCycles, 0);
        Interlocked.Exchange(ref lastWarmupDurationTicks, 0);
        Interlocked.Exchange(ref warmupRepeatTicks, 0);
        Interlocked.Exchange(ref lastWarmupRepeatTicks, 0);
        lastSentFrame?.Dispose();
        lastSentFrame = null;
    }

    private void ExitWarmup()
    {
        if (Interlocked.Exchange(ref warmupState, 0) == 1)
        {
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
            latencyError = Math.Max(0, latencyError);
        }
    }

    private void UpdateWarmupState(int backlog)
    {
        if (lastSentFrame is null)
        {
            return;
        }

        latencyError += backlog - targetDepth;
        Interlocked.Increment(ref warmupRepeatTicks);
    }

    private void TrimBacklogIfNeeded()
    {
        if (ringBuffer is null)
        {
            return;
        }

        var highLimit = (int)Math.Ceiling(highWatermark);

        while (latencyError > 1 && ringBuffer.Count > targetDepthFrames && ringBuffer.TryDropOldestAsStale(out var excess))
        {
            excess.Dispose();
            latencyError -= 1;
        }

        while (ringBuffer.Count > highLimit && ringBuffer.TryDropOldestAsStale(out var trimmed))
        {
            trimmed.Dispose();
            latencyError -= 1;
        }
    }

    private double ComputeLastWarmupMilliseconds()
    {
        var ticks = Interlocked.Read(ref lastWarmupDurationTicks);
        return ticks <= 0 ? 0 : ticks / (double)TimeSpan.TicksPerMillisecond;
    }

    private bool IsInWarmup => Volatile.Read(ref warmupState) == 1;

    internal bool BufferPrimed => !IsInWarmup;

    internal long BufferUnderruns => Interlocked.Read(ref underruns);

    internal TimeSpan LastWarmupDuration => TimeSpan.FromTicks(Interlocked.Read(ref lastWarmupDurationTicks));

    internal long LastWarmupRepeatTicks => Interlocked.Read(ref lastWarmupRepeatTicks);

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
            ? $", primed={!IsInWarmup}, buffered={ringBuffer.Count}, droppedOverflow={ringBuffer.DroppedFromOverflow}, droppedStale={ringBuffer.DroppedAsStale}, underruns={Interlocked.Read(ref underruns)}, warmups={Interlocked.Read(ref warmupCycles)}, lastWarmupMs={ComputeLastWarmupMilliseconds():F1}, lastWarmupRepeats={Interlocked.Read(ref lastWarmupRepeatTicks)}, latencyError={Volatile.Read(ref latencyError):F2}"
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
