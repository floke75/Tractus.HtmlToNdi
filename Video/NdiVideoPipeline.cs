using NewTek;
using NewTek.NDI;
using Serilog;
using System.Runtime.CompilerServices;

namespace Tractus.HtmlToNdi.Video;

internal sealed class NdiVideoPipeline : IDisposable
{
    private readonly INdiVideoSender sender;
    private readonly FrameRate configuredFrameRate;
    private readonly NdiVideoPipelineOptions options;
    private readonly FrameTimeAverager timeAverager = new();
    private readonly CancellationTokenSource cancellation = new();
    private readonly FrameRingBuffer<NdiVideoFrame>? ringBuffer;
    private readonly SemaphoreSlim frameAvailableSignal;
    private readonly ILogger logger;

    private Task? pacingTask;
    private NdiVideoFrame? lastSentFrame;
    private long capturedFrames;
    private long sentFrames;
    private long repeatedFrames;
    private long underruns;
    private bool isBufferPrimed;
    private TimeSpan lastWarmupDuration = TimeSpan.Zero;
    private DateTime lastTelemetry = DateTime.UtcNow;

    public NdiVideoPipeline(INdiVideoSender sender, FrameRate frameRate, NdiVideoPipelineOptions options, ILogger logger)
    {
        this.sender = sender ?? throw new ArgumentNullException(nameof(sender));
        configuredFrameRate = frameRate;
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.logger = logger;

        if (options.EnableBuffering)
        {
            var bufferDepth = Math.Max(1, options.BufferDepth);
            ringBuffer = new FrameRingBuffer<NdiVideoFrame>(bufferDepth);
            frameAvailableSignal = new SemaphoreSlim(0, bufferDepth);
        }
        else
        {
            frameAvailableSignal = new SemaphoreSlim(0, 1);
        }
    }

    public bool BufferingEnabled => options.EnableBuffering;

    public FrameRate FrameRate => configuredFrameRate;

    public bool IsBufferPrimed => isBufferPrimed;

    public long UnderrunCount => Interlocked.Read(ref underruns);

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
            pacingTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex) when (ex is TaskCanceledException || ex is AggregateException)
        {
            // ignore
        }

        pacingTask = null;
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

        if (frameAvailableSignal.CurrentCount < ringBuffer.Capacity)
        {
            frameAvailableSignal.Release();
        }

        EmitTelemetryIfNeeded();
    }

    internal void ProcessPacingTick() // for testing only
    {
        if (ringBuffer is null)
        {
            return;
        }

        if (!isBufferPrimed)
        {
            if (ringBuffer.Count >= ringBuffer.Capacity)
            {
                isBufferPrimed = true;
            }
            else
            {
                return;
            }
        }

        if (ringBuffer.TryDequeue(out var frame))
        {
            SendBufferedFrame(frame);
        }
        else
        {
            Interlocked.Increment(ref underruns);
            isBufferPrimed = false;
            RepeatLastFrame();
        }
    }

    private async Task RunPacedLoopAsync(CancellationToken token)
    {
        var warmupStart = DateTime.UtcNow;
        var timer = new PeriodicTimer(FrameRate.FrameDuration);

        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                if (token.IsCancellationRequested)
                {
                    break;
                }

                if (ringBuffer is null)
                {
                    continue;
                }

                if (!isBufferPrimed)
                {
                    // STATE: WARMING UP
                    if (ringBuffer.Count >= ringBuffer.Capacity)
                    {
                        isBufferPrimed = true;
                        lastWarmupDuration = DateTime.UtcNow - warmupStart;
                        logger.Information("Paced pipeline primed after {Duration}ms.", lastWarmupDuration.TotalMilliseconds);
                    }
                    else
                    {
                        logger.Debug("Paced pipeline is warming up (backlog: {Count}/{Capacity})", ringBuffer.Count, ringBuffer.Capacity);
                        continue;
                    }
                }

                // STATE: PRIMED AND RUNNING
                if (frameAvailableSignal.Wait(0))
                {
                    if (ringBuffer.TryDequeue(out var frame))
                    {
                        SendBufferedFrame(frame);
                    }
                    else
                    {
                        // This indicates a logic error - semaphore and queue are out of sync.
                        Interlocked.Increment(ref underruns);
                        isBufferPrimed = false; // Force re-warm
                        warmupStart = DateTime.UtcNow;
                        RepeatLastFrame();
                    }
                }
                else
                {
                    // Underrun: pacer is faster than producer.
                    Interlocked.Increment(ref underruns);
                    isBufferPrimed = false;
                    warmupStart = DateTime.UtcNow;
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
            ? $", primed={isBufferPrimed}, buffered={ringBuffer.Count}, underruns={Interlocked.Read(ref underruns)}, warmupMs={lastWarmupDuration.TotalMilliseconds:F0}, droppedOverflow={ringBuffer.DroppedFromOverflow}, droppedStale={ringBuffer.DroppedAsStale}"
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
        frameAvailableSignal.Dispose();
    }
}
