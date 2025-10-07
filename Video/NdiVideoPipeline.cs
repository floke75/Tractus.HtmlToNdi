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
    private readonly int targetBufferDepth;

    private Task? pacingTask;
    private NdiVideoFrame? lastSentFrame;
    private long capturedFrames;
    private long sentFrames;
    private long repeatedFrames;
    private long underruns;
    private long warmupCycles;
    private long lastWarmupDurationTicks;
    private int bufferPrimedFlag;
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
            targetBufferDepth = Math.Max(1, options.BufferDepth);
            ringBuffer = new FrameRingBuffer<NdiVideoFrame>(targetBufferDepth);
        }
        else
        {
            targetBufferDepth = 0;
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

        warmupStart = DateTime.UtcNow;
        Volatile.Write(ref bufferPrimedFlag, 0);
        Interlocked.Exchange(ref lastWarmupDurationTicks, 0);
        Interlocked.Exchange(ref underruns, 0);
        Interlocked.Exchange(ref warmupCycles, 0);

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
                ProcessPacedTick(ringBuffer, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // expected during shutdown
        }
        finally
        {
            timer.Dispose();
        }
    }

    private void ProcessPacedTick(FrameRingBuffer<NdiVideoFrame> buffer, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        var backlog = buffer.Count;

        if (!IsBufferPrimed)
        {
            if (backlog >= targetBufferDepth)
            {
                PromoteBufferToPrimed();
                backlog = buffer.Count;
            }
            else
            {
                RepeatLastFrame();
                return;
            }
        }

        if (backlog < targetBufferDepth)
        {
            HandleUnderrun(buffer);
            return;
        }

        if (!buffer.TryDequeue(out var frame) || frame is null)
        {
            HandleUnderrun(buffer);
            return;
        }

        SendBufferedFrame(frame);
    }

    private bool IsBufferPrimed => Volatile.Read(ref bufferPrimedFlag) == 1;

    private void PromoteBufferToPrimed()
    {
        Volatile.Write(ref bufferPrimedFlag, 1);

        if (warmupStart is DateTime started)
        {
            var duration = DateTime.UtcNow - started;
            if (duration < TimeSpan.Zero)
            {
                duration = TimeSpan.Zero;
            }

            Interlocked.Exchange(ref lastWarmupDurationTicks, duration.Ticks);
            warmupStart = null;
        }

        Interlocked.Increment(ref warmupCycles);
    }

    private void HandleUnderrun(FrameRingBuffer<NdiVideoFrame> buffer)
    {
        Interlocked.Increment(ref underruns);

        DrainResidualFrames(buffer);
        RepeatLastFrame();
        BeginWarmup();
    }

    private void DrainResidualFrames(FrameRingBuffer<NdiVideoFrame> buffer)
    {
        NdiVideoFrame? latest = null;

        while (buffer.TryDequeue(out var candidate))
        {
            if (candidate is null)
            {
                continue;
            }

            latest?.Dispose();
            latest = candidate;
        }

        if (latest is not null)
        {
            lastSentFrame?.Dispose();
            lastSentFrame = latest;
        }
    }

    private void BeginWarmup()
    {
        Volatile.Write(ref bufferPrimedFlag, 0);
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
            ? $", primed={IsBufferPrimed}, backlog={ringBuffer.Count}, droppedOverflow={ringBuffer.DroppedFromOverflow}, droppedStale={ringBuffer.DroppedAsStale}, underruns={Interlocked.Read(ref underruns)}, warmups={Interlocked.Read(ref warmupCycles)}, lastWarmupMs={ComputeLastWarmupMilliseconds():F0}"
            : string.Empty;

        logger.Information(
            "NDI video pipeline stats: captured={Captured}, sent={Sent}, repeated={Repeated}{BufferStats} (caller={Caller})",
            Interlocked.Read(ref capturedFrames),
            Interlocked.Read(ref sentFrames),
            Interlocked.Read(ref repeatedFrames),
            bufferStats,
            caller);
    }

    private double ComputeLastWarmupMilliseconds()
    {
        var ticks = Interlocked.Read(ref lastWarmupDurationTicks);
        return ticks <= 0 ? 0 : ticks / (double)TimeSpan.TicksPerMillisecond;
    }

    public void Dispose()
    {
        Stop();
        ringBuffer?.Clear();
        lastSentFrame?.Dispose();
        cancellation.Dispose();
    }
}
