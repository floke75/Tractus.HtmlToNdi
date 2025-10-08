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
            // Exit warm-up only when we have the full buffer and have paid back any latency debt.
            if (backlog >= targetDepth && latencyError >= 0)
            {
                isWarmingUp = false;
                var warmupDuration = DateTime.UtcNow - warmupStart;
                logger.Information(
                    "Paced buffer priming complete. Resuming normal pacing. " +
                    "(duration={DurationMs}ms, repeatedFrames={RepeatedFrames})",
                    warmupDuration.TotalMilliseconds,
                    repeatedFramesInCurrentWarmup);
                repeatedFramesInCurrentWarmup = 0;
            }
            else
            {
                // Still warming up, so repeat the last frame.
                return false;
            }
        }

        // After this point, we are in the normal pacing state.
        // Check for underrun condition.
        if (backlog < lowWatermark)
        {
            logger.Warning(
                "Paced buffer underrun detected (backlog={Backlog}, target={TargetDepth}). Repeating last frame and re-priming.",
                backlog, targetDepth);
            isWarmingUp = true;
            warmupStart = DateTime.UtcNow;
            latencyError = 0;
            Interlocked.Increment(ref underrunEvents);
            ringBuffer.DrainToLatestAndKeep();
            return false;
        }

        // High watermark check: if we are accumulating latency, drop a frame.
        if (latencyError > 1.0 && ringBuffer.Count > targetDepth)
        {
            if (ringBuffer.TryDequeue(out var droppedFrame))
            {
                droppedFrame.Dispose();
                latencyError--;
                logger.Verbose("Paced buffer latency integrator triggered frame drop to reduce delay.");
            }
        }

        if (ringBuffer.TryDequeue(out var frame))
        {
            SendBufferedFrame(frame);
            return true;
        }

        // Should be rare, but if dequeue fails, enter warm-up.
        isWarmingUp = true;
        warmupStart = DateTime.UtcNow;
        Interlocked.Increment(ref underrunEvents);
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

    private void EmitTelemetryIfNeeded([CallerMemberName] string? caller = null)
    {
        if (DateTime.UtcNow - lastTelemetry < options.TelemetryInterval)
        {
            return;
        }

        lastTelemetry = DateTime.UtcNow;

        var bufferStats = BufferingEnabled && ringBuffer is not null
            ? $", warmingUp={isWarmingUp}, buffered={ringBuffer.Count}, latencyError={latencyError:F2}, underruns={Interlocked.Read(ref underrunEvents)}, droppedOverflow={ringBuffer.DroppedFromOverflow}, droppedStale={ringBuffer.DroppedAsStale}"
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
