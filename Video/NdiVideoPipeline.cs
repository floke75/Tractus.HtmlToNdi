using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using CefSharp;
using NewTek;
using NewTek.NDI;
using Serilog;

namespace Tractus.HtmlToNdi.Video;

public sealed class NdiVideoPipeline : IDisposable
{
    private readonly nint ndiSender;
    private readonly FrameRate frameRate;
    private readonly ILogger logger;
    private readonly FrameTimeAverager frameTimeAverager = new();
    private readonly FrameRingBuffer? frameBuffer;
    private readonly CancellationTokenSource? pacerCts;
    private readonly Task? pacerTask;
    private readonly TimeSpan framePeriod;
    private readonly Timer metricsTimer;
    private BufferedVideoFrame? lastFrame;
    private long repeatedFrames;
    private readonly object sendSync = new();
    private readonly bool isBuffered;

    public NdiVideoPipeline(nint ndiSender, FrameRate frameRate, int bufferDepth, ILogger logger)
    {
        this.ndiSender = ndiSender;
        this.frameRate = frameRate;
        this.logger = logger.ForContext<NdiVideoPipeline>();
        framePeriod = frameRate.FrameDuration;
        isBuffered = bufferDepth > 0;

        if (bufferDepth > 0)
        {
            frameBuffer = new FrameRingBuffer(bufferDepth);
            pacerCts = new CancellationTokenSource();
            pacerTask = Task.Run(() => PaceAsync(pacerCts.Token));
        }

        metricsTimer = new Timer(LogMetrics, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
    }

    public bool IsBuffered => isBuffered;

    public long DroppedFrames => frameBuffer?.DroppedFrames ?? 0;

    public long RepeatedFrames => Interlocked.Read(ref repeatedFrames);

    public void HandleFrame(OnPaintEventArgs paintArgs, DateTime timestampUtc)
    {
        frameTimeAverager.AddSample(timestampUtc);

        if (!isBuffered)
        {
            SendDirect(paintArgs, timestampUtc);
            return;
        }

        var stride = paintArgs.Width * 4;
        var frame = BufferedVideoFrame.Rent(stride * paintArgs.Height);
        frame.Populate(paintArgs.Width, paintArgs.Height, stride, timestampUtc);

        unsafe
        {
            using var pin = frame.Memory.Pin();
            Buffer.MemoryCopy((void*)paintArgs.BufferHandle, pin.Pointer, frame.Length, frame.Length);
        }

        frameBuffer!.Enqueue(frame);
    }

    private void SendDirect(OnPaintEventArgs paintArgs, DateTime timestampUtc)
    {
        if (ndiSender == nint.Zero)
        {
            return;
        }

        var videoFrame = new NDIlib.video_frame_v2_t
        {
            FourCC = NDIlib.FourCC_type_e.FourCC_type_BGRA,
            frame_rate_N = frameRate.Numerator,
            frame_rate_D = frameRate.Denominator,
            frame_format_type = NDIlib.frame_format_type_e.frame_format_type_progressive,
            line_stride_in_bytes = paintArgs.Width * 4,
            picture_aspect_ratio = (float)paintArgs.Width / paintArgs.Height,
            p_data = paintArgs.BufferHandle,
            timecode = NDIlib.send_timecode_synthesize,
            xres = paintArgs.Width,
            yres = paintArgs.Height
        };

        lock (sendSync)
        {
            NDIlib.send_send_video_v2(ndiSender, ref videoFrame);
        }
    }

    private async Task PaceAsync(CancellationToken cancellationToken)
    {
        var timer = new PeriodicTimer(framePeriod);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                BufferedVideoFrame? frame = null;

                try
                {
                    frame = frameBuffer!.TakeLatest();
                    if (frame is null)
                    {
                        if (lastFrame is not null)
                        {
                            Interlocked.Increment(ref repeatedFrames);
                            await SendBufferedAsync(lastFrame, isRepeat: true).ConfigureAwait(false);
                        }

                        continue;
                    }

                    lastFrame?.Dispose();
                    lastFrame = frame;
                    await SendBufferedAsync(frame, isRepeat: false).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.Warning(ex, "Buffered pacing tick failed");
                    frame?.Dispose();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // expected
        }
        finally
        {
            timer.Dispose();
        }
    }

    private async ValueTask SendBufferedAsync(BufferedVideoFrame frame, bool isRepeat)
    {
        if (ndiSender == nint.Zero)
        {
            return;
        }

        unsafe
        {
            using var pin = frame.Memory.Pin();
            var videoFrame = new NDIlib.video_frame_v2_t
            {
                FourCC = NDIlib.FourCC_type_e.FourCC_type_BGRA,
                frame_rate_N = frameRate.Numerator,
                frame_rate_D = frameRate.Denominator,
                frame_format_type = NDIlib.frame_format_type_e.frame_format_type_progressive,
                line_stride_in_bytes = frame.Stride,
                picture_aspect_ratio = (float)frame.Width / frame.Height,
                p_data = (nint)pin.Pointer,
                timecode = NDIlib.send_timecode_synthesize,
                xres = frame.Width,
                yres = frame.Height
            };

            lock (sendSync)
            {
                NDIlib.send_send_video_v2(ndiSender, ref videoFrame);
            }
        }

        if (!isRepeat)
        {
            await Task.Yield();
        }
    }

    private void LogMetrics(object? state)
    {
        var measuredFps = frameTimeAverager.GetAverageFps();
        var dropped = DroppedFrames;
        var repeats = RepeatedFrames;

        logger.Information("NDI pacing metrics: target={TargetFps:F3}fps measured={MeasuredFps:F3}fps buffered={Buffered} dropped={Dropped} repeated={Repeated}", frameRate.Hertz, measuredFps, isBuffered, dropped, repeats);

        TrySendMetadata(measuredFps, dropped, repeats);
    }

    private void TrySendMetadata(double measuredFps, long dropped, long repeated)
    {
        if (ndiSender == nint.Zero)
        {
            return;
        }

        var metadataXml = $"<ndi_frame_metrics target_fps=\"{frameRate.Hertz:F3}\" measured_fps=\"{measuredFps:F3}\" buffered=\"{isBuffered}\" dropped=\"{dropped}\" repeated=\"{repeated}\" />\0";
        var ptr = UTF.StringToUtf8(metadataXml);
        try
        {
            var metadataFrame = new NDIlib.metadata_frame_t
            {
                p_data = ptr
            };

            NDIlib.send_send_metadata(ndiSender, ref metadataFrame);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    public void Dispose()
    {
        metricsTimer.Dispose();

        if (isBuffered)
        {
            pacerCts!.Cancel();
            try
            {
                pacerTask?.Wait();
            }
            catch (AggregateException ex) when (ex.InnerException is OperationCanceledException)
            {
                // ignore
            }

            pacerCts.Dispose();
            frameBuffer!.Clear();
            lastFrame?.Dispose();
            lastFrame = null;
        }
    }
}
