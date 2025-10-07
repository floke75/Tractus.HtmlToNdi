using NewTek;
using NewTek.NDI;
using Serilog;
using System.Globalization;
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
    private readonly ILogger logger;
    private readonly int warmupThreshold;

    private Task? pacingTask;
    private NdiVideoFrame? lastSentFrame;
    private long capturedFrames;
    private long sentFrames;
    private long repeatedFrames;
    private long underrunCount;
    private long lastWarmupDurationTicks;
    private long warmupStartTicks;
    private volatile bool bufferPrimed;
    private DateTime lastTelemetry = DateTime.UtcNow;

    public NdiVideoPipeline(INdiVideoSender sender, FrameRate frameRate, NdiVideoPipelineOptions options, ILogger logger)
    {
        this.sender = sender ?? throw new ArgumentNullException(nameof(sender));
        configuredFrameRate = frameRate;
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.logger = logger;

        warmupThreshold = Math.Max(1, options.BufferDepth);

        if (options.EnableBuffering)
        {
            ringBuffer = new FrameRingBuffer<NdiVideoFrame>(warmupThreshold);
        }

        warmupStartTicks = DateTime.UtcNow.Ticks;
    }

    public bool BufferingEnabled => options.EnableBuffering;

    public FrameRate FrameRate => configuredFrameRate;

    public void Start()
    {
        if (!BufferingEnabled || pacingTask != null)
        {
            return;
        }

        ResetWarmupState();
        Interlocked.Exchange(ref underrunCount, 0);
        Interlocked.Exchange(ref lastWarmupDurationTicks, 0);

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
        var underfilledTicks = 0;
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
                    RepeatLastFrame();
                    continue;
                }

                var count = ringBuffer.Count;

                if (!bufferPrimed)
                {
                    if (count >= warmupThreshold)
                    {
                        bufferPrimed = true;
                        underfilledTicks = 0;
                        RecordWarmupCompleted();
                    }
                    else
                    {
                        continue;
                    }
                }
                else
                {
                    if (count >= warmupThreshold)
                    {
                        underfilledTicks = 0;
                    }
                    else
                    {
                        underfilledTicks++;
                        if (underfilledTicks > 1)
                        {
                            ResetWarmupState();
                            underfilledTicks = 0;
                            continue;
                        }
                    }
                }

                if (ringBuffer.TryDequeue(out var frame) && frame is not null)
                {
                    SendBufferedFrame(frame);
                    continue;
                }

                RegisterUnderrun();
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

    private void RegisterUnderrun()
    {
        Interlocked.Increment(ref underrunCount);
        RepeatLastFrame();
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

        var bufferStats = string.Empty;
        if (BufferingEnabled && ringBuffer is not null)
        {
            var buffered = ringBuffer.Count;
            var primed = bufferPrimed;
            var underruns = Interlocked.Read(ref underrunCount);
            var warmupMs = TimeSpan.FromTicks(Interlocked.Read(ref lastWarmupDurationTicks)).TotalMilliseconds;
            bufferStats = string.Create(
                CultureInfo.InvariantCulture,
                ", primed={0}, buffered={1}, underruns={2}, warmupMs={3:F0}, droppedOverflow={4}, droppedStale={5}",
                primed,
                buffered,
                underruns,
                warmupMs,
                ringBuffer.DroppedFromOverflow,
                ringBuffer.DroppedAsStale);
        }

        logger.Information(
            "NDI video pipeline stats: captured={Captured}, sent={Sent}, repeated={Repeated}{BufferStats} (caller={Caller})",
            Interlocked.Read(ref capturedFrames),
            Interlocked.Read(ref sentFrames),
            Interlocked.Read(ref repeatedFrames),
            bufferStats,
            caller);
    }

    internal bool BufferPrimed => bufferPrimed;

    internal long BufferUnderruns => Interlocked.Read(ref underrunCount);

    internal TimeSpan LastWarmupDuration => TimeSpan.FromTicks(Interlocked.Read(ref lastWarmupDurationTicks));

    private void ResetWarmupState()
    {
        bufferPrimed = false;
        Interlocked.Exchange(ref warmupStartTicks, DateTime.UtcNow.Ticks);
    }

    private void RecordWarmupCompleted()
    {
        var startTicks = Interlocked.Read(ref warmupStartTicks);
        if (startTicks <= 0)
        {
            Interlocked.Exchange(ref lastWarmupDurationTicks, 0);
            return;
        }

        var start = new DateTime(startTicks, DateTimeKind.Utc);
        var duration = DateTime.UtcNow - start;
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        Interlocked.Exchange(ref lastWarmupDurationTicks, duration.Ticks);
    }

    public void Dispose()
    {
        Stop();
        ringBuffer?.Clear();
        lastSentFrame?.Dispose();
        cancellation.Dispose();
    }
}
