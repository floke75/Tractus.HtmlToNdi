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
    private readonly SemaphoreSlim framesAvailable = new(0);
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan nominalInterval;
    private TimeSpan pacedInterval;
    private DateTimeOffset? lastCapturedTimestamp;

    private Task? pacingTask;
    private NdiVideoFrame? lastSentFrame;
    private long capturedFrames;
    private long sentFrames;
    private long repeatedFrames;
    private DateTime lastTelemetry = DateTime.UtcNow;

    public NdiVideoPipeline(
        INdiVideoSender sender,
        FrameRate frameRate,
        NdiVideoPipelineOptions options,
        ILogger logger,
        TimeProvider? timeProvider = null)
    {
        this.sender = sender ?? throw new ArgumentNullException(nameof(sender));
        configuredFrameRate = frameRate;
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.logger = logger;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        nominalInterval = frameRate.FrameDuration;
        pacedInterval = nominalInterval;

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

        pacedInterval = nominalInterval;
        lastCapturedTimestamp = null;
        pacingTask = Task.Run(async () => await RunPacedLoopAsync(cancellation.Token));
    }

    public void Stop()
    {
        cancellation.Cancel();
        if (BufferingEnabled)
        {
            try
            {
                framesAvailable.Release();
            }
            catch (SemaphoreFullException)
            {
                // already signalled
            }
        }
        try
        {
            pacingTask?.Wait();
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

        NdiVideoFrame? dropped = null;
        var copy = NdiVideoFrame.CopyFrom(frame);
        ringBuffer.Enqueue(copy, out dropped);
        dropped?.Dispose();
        if (ringBuffer.Count == 1)
        {
            try
            {
                framesAvailable.Release();
            }
            catch (SemaphoreFullException)
            {
                // already signalled
            }
        }
        EmitTelemetryIfNeeded();
    }

    private async Task RunPacedLoopAsync(CancellationToken token)
    {
        if (ringBuffer is null)
        {
            return;
        }

        var nextPresentation = timeProvider.GetUtcNow();

        try
        {
            while (!token.IsCancellationRequested)
            {
                var now = timeProvider.GetUtcNow();
                var signalledEarly = false;

                if (now < nextPresentation)
                {
                    var delay = nextPresentation - now;
                    if (delay > TimeSpan.Zero)
                    {
                        using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                        delayCts.CancelAfter(delay);
                        try
                        {
                            await framesAvailable.WaitAsync(delayCts.Token);
                            signalledEarly = true;
                            nextPresentation = timeProvider.GetUtcNow();
                        }
                        catch (OperationCanceledException) when (!token.IsCancellationRequested && delayCts.IsCancellationRequested)
                        {
                            // timeout – fall through to send on schedule
                        }
                    }
                }
                else
                {
                    signalledEarly = framesAvailable.Wait(0);
                }

                var frame = ringBuffer.DequeueLatest();
                if (frame is null)
                {
                    if (lastSentFrame is not null)
                    {
                        RepeatLastFrame();
                        nextPresentation += pacedInterval;
                        continue;
                    }

                    if (!signalledEarly)
                    {
                        await framesAvailable.WaitAsync(token);
                    }

                    nextPresentation = timeProvider.GetUtcNow();
                    continue;
                }

                if (!signalledEarly && framesAvailable.CurrentCount > 0)
                {
                    framesAvailable.Wait(0);
                }

                UpdatePacedInterval(frame.Timestamp);
                SendBufferedFrame(frame);

                nextPresentation += pacedInterval;

                var afterSend = timeProvider.GetUtcNow();
                if (afterSend - nextPresentation > pacedInterval)
                {
                    nextPresentation = afterSend;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    private void UpdatePacedInterval(DateTime capturedUtc)
    {
        var captured = new DateTimeOffset(DateTime.SpecifyKind(capturedUtc, DateTimeKind.Utc));
        if (lastCapturedTimestamp is DateTimeOffset last)
        {
            var delta = captured - last;
            if (delta <= TimeSpan.Zero)
            {
                delta = nominalInterval;
            }

            var errorTicks = delta.Ticks - nominalInterval.Ticks;
            var adjustmentTicks = (long)(errorTicks * 0.25);
            var newTicks = nominalInterval.Ticks + adjustmentTicks;
            var minTicks = (long)(nominalInterval.Ticks * 0.5);
            var maxTicks = (long)(nominalInterval.Ticks * 1.5);
            pacedInterval = TimeSpan.FromTicks(Math.Clamp(newTicks, minTicks, maxTicks));
        }
        else
        {
            pacedInterval = nominalInterval;
        }

        lastCapturedTimestamp = captured;
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
            ? $", buffered={ringBuffer.Count}, droppedOverflow={ringBuffer.DroppedFromOverflow}, droppedStale={ringBuffer.DroppedAsStale}"
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
        framesAvailable.Dispose();
    }
}
