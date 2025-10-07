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

    private Task? pacingTask;
    private NdiVideoFrame? lastSentFrame;
    private long capturedFrames;
    private long sentFrames;
    private long repeatedFrames;
    private long underruns;
    private long warmupCycles;
    private long lastWarmupDurationMilliseconds;
    private int bufferPrimedFlag;
    private int consecutiveBelowThresholdTicks;
    private DateTime? warmupStart;
    private DateTime lastTelemetry = DateTime.UtcNow;

    public NdiVideoPipeline(INdiVideoSender sender, FrameRate frameRate, NdiVideoPipelineOptions options, ILogger logger)
    {
        this.sender = sender ?? throw new ArgumentNullException(nameof(sender));
        configuredFrameRate = frameRate;
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.logger = logger;

        if (options.EnableBuffering)
        {
            ringBuffer = new FrameRingBuffer<NdiVideoFrame>(Math.Max(1, options.BufferDepth));
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
        Volatile.Write(ref bufferPrimedFlag, 0);
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
        if (ringBuffer is null)
        {
            return;
        }

        var timer = new PeriodicTimer(FrameRate.FrameDuration);
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                if (token.IsCancellationRequested)
                {
                    break;
                }

                ProcessPacedTick(ringBuffer, token);
            }
        }
        finally
        {
            timer.Dispose();
        }
    }

    private void ProcessPacedTick(FrameRingBuffer<NdiVideoFrame> buffer, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        var targetDepth = buffer.Capacity;
        var count = buffer.Count;

        if (!IsBufferPrimed)
        {
            warmupStart ??= DateTime.UtcNow;

            if (count < targetDepth)
            {
                return;
            }

            PromoteBufferToPrimed();
            count = buffer.Count;
        }
        var shouldRewarm = false;

        if (count < targetDepth)
        {
            consecutiveBelowThresholdTicks++;
            if (consecutiveBelowThresholdTicks > 1)
            {
                shouldRewarm = true;
            }
        }
        else
        {
            consecutiveBelowThresholdTicks = 0;
        }

        if (buffer.TryDequeue(out var frame) && frame is not null)
        {
            SendBufferedFrame(frame);
        }
        else
        {
            Interlocked.Increment(ref underruns);

            warmupStart ??= DateTime.UtcNow;

            if (lastSentFrame is not null)
            {
                RepeatLastFrame();
            }
            else
            {
                EmitTelemetryIfNeeded();
            }

            shouldRewarm = true;
        }

        if (shouldRewarm)
        {
            BeginRewarm();
        }
    }

    private bool IsBufferPrimed => Volatile.Read(ref bufferPrimedFlag) == 1;

    private void PromoteBufferToPrimed()
    {
        Volatile.Write(ref bufferPrimedFlag, 1);
        consecutiveBelowThresholdTicks = 0;

        if (warmupStart.HasValue)
        {
            var duration = DateTime.UtcNow - warmupStart.Value;
            Interlocked.Increment(ref warmupCycles);
            Interlocked.Exchange(ref lastWarmupDurationMilliseconds, (long)Math.Round(duration.TotalMilliseconds));
            warmupStart = null;
        }
    }

    private void BeginRewarm()
    {
        Volatile.Write(ref bufferPrimedFlag, 0);
        consecutiveBelowThresholdTicks = 0;
        warmupStart = DateTime.UtcNow;
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
            ? $", primed={IsBufferPrimed}, backlog={ringBuffer.Count}, droppedOverflow={ringBuffer.DroppedFromOverflow}, droppedStale={ringBuffer.DroppedAsStale}, underruns={Interlocked.Read(ref underruns)}, warmups={Interlocked.Read(ref warmupCycles)}, lastWarmupMs={Interlocked.Read(ref lastWarmupDurationMilliseconds)}"
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
