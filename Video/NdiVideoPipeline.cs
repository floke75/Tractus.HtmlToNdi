using System;
using System.Threading;
using System.Threading.Tasks;

using System.Buffers;
using CefSharp.OffScreen;
using NewTek;
using NewTek.NDI;
using Serilog;

namespace Tractus.HtmlToNdi.Video;

public sealed class NdiVideoPipeline : INdiVideoSink, IDisposable
{
    private readonly nint _ndiSender;
    private readonly FrameRate _targetFrameRate;
    private readonly bool _useBuffer;
    private readonly int _bufferDepth;
    private readonly ILogger _logger;
    private readonly FramePump _framePump;
    private readonly FrameTimeAverager _timeAverager = new();
    private readonly CancellationTokenSource? _pacerCts;
    private readonly Task? _pacerTask;
    private readonly Queue<BufferedVideoFrame> _bufferQueue;
    private readonly object _gate = new();
    private BufferedVideoFrame? _lastDelivered;
    private long _framesSent;
    private long _framesDropped;
    private long _framesRepeated;
    private DateTime _lastTelemetry = DateTime.UtcNow;
    private readonly TimeSpan _telemetryInterval = TimeSpan.FromSeconds(15);

    public NdiVideoPipeline(nint ndiSender, FrameRate targetFrameRate, bool useBuffer, int bufferDepth, ILogger logger, FramePump framePump)
    {
        _ndiSender = ndiSender;
        _targetFrameRate = targetFrameRate;
        _useBuffer = useBuffer;
        _bufferDepth = Math.Max(1, bufferDepth);
        _logger = logger;
        _framePump = framePump;
        _bufferQueue = new Queue<BufferedVideoFrame>(_bufferDepth);

        if (_useBuffer)
        {
            _pacerCts = new CancellationTokenSource();
            _pacerTask = Task.Run(() => RunPacerAsync(_pacerCts.Token));
        }
    }

    public void HandleFrame(OnPaintEventArgs args)
    {
        if (!_useBuffer)
        {
            SendDirect(args);
            return;
        }

        var dataLength = args.Width * args.Height * 4;
        var memoryOwner = MemoryPool<byte>.Shared.Rent(dataLength);
        var memory = memoryOwner.Memory.Span[..dataLength];

        unsafe
        {
            fixed (byte* destination = memory)
            {
                Buffer.MemoryCopy((void*)args.BufferHandle, destination, dataLength, dataLength);
            }
        }

        var frame = new BufferedVideoFrame(memoryOwner, args.Width, args.Height, args.Width * 4, dataLength);

        BufferedVideoFrame? dropped = null;
        lock (_gate)
        {
            if (_bufferQueue.Count >= _bufferDepth)
            {
                dropped = _bufferQueue.Dequeue();
                Interlocked.Increment(ref _framesDropped);
            }

            _bufferQueue.Enqueue(frame);
        }

        dropped?.Dispose();
    }

    private void SendDirect(OnPaintEventArgs args)
    {
        var now = DateTime.UtcNow;
        _timeAverager.Observe(now);
        var metadataRate = _timeAverager.GetFrameRate(_targetFrameRate);

        var videoFrame = new NDIlib.video_frame_v2_t
        {
            FourCC = NDIlib.FourCC_type_e.FourCC_type_BGRA,
            frame_rate_N = metadataRate.Numerator,
            frame_rate_D = metadataRate.Denominator,
            frame_format_type = NDIlib.frame_format_type_e.frame_format_type_progressive,
            line_stride_in_bytes = args.Width * 4,
            picture_aspect_ratio = (float)args.Width / args.Height,
            p_data = args.BufferHandle,
            timecode = NDIlib.send_timecode_synthesize,
            xres = args.Width,
            yres = args.Height,
        };

        NDIlib.send_send_video_v2(_ndiSender, ref videoFrame);
        _framePump.NotifyFrameDelivered();
        Interlocked.Increment(ref _framesSent);
        EmitTelemetryIfNeeded();
    }

    private async Task RunPacerAsync(CancellationToken cancellationToken)
    {
        var timer = new PeriodicTimer(_targetFrameRate.FrameDuration);

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                BufferedVideoFrame? frame = null;
                var repeat = false;

                lock (_gate)
                {
                    if (_bufferQueue.Count > 0)
                    {
                        frame = _bufferQueue.Dequeue();
                        repeat = false;
                    }
                    else if (_lastDelivered is not null)
                    {
                        frame = _lastDelivered;
                        repeat = true;
                    }
                }

                if (frame is null)
                {
                    continue;
                }

                if (!repeat)
                {
                    var toDispose = Interlocked.Exchange(ref _lastDelivered, frame);
                    toDispose?.Dispose();
                }

                SendBufferedFrame(frame, repeat);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            timer.Dispose();
        }
    }

    private void SendBufferedFrame(BufferedVideoFrame frame, bool isRepeat)
    {
        using var handle = frame.Pin();

        var now = DateTime.UtcNow;
        _timeAverager.Observe(now);
        var metadataRate = _timeAverager.GetFrameRate(_targetFrameRate);

        var videoFrame = new NDIlib.video_frame_v2_t
        {
            FourCC = NDIlib.FourCC_type_e.FourCC_type_BGRA,
            frame_rate_N = metadataRate.Numerator,
            frame_rate_D = metadataRate.Denominator,
            frame_format_type = NDIlib.frame_format_type_e.frame_format_type_progressive,
            line_stride_in_bytes = frame.Stride,
            picture_aspect_ratio = (float)frame.Width / frame.Height,
            p_data = (nint)handle.Pointer!,
            timecode = NDIlib.send_timecode_synthesize,
            xres = frame.Width,
            yres = frame.Height,
        };

        NDIlib.send_send_video_v2(_ndiSender, ref videoFrame);

        if (!isRepeat)
        {
            _framePump.NotifyFrameDelivered();
        }
        else
        {
            Interlocked.Increment(ref _framesRepeated);
        }

        Interlocked.Increment(ref _framesSent);
        EmitTelemetryIfNeeded();
    }

    private void EmitTelemetryIfNeeded()
    {
        var now = DateTime.UtcNow;
        if (now - _lastTelemetry < _telemetryInterval)
        {
            return;
        }

        _lastTelemetry = now;
        _logger.Information("NDI pipeline stats | sent={FramesSent} dropped={Dropped} repeats={Repeats} mode={Mode}",
            Interlocked.Read(ref _framesSent),
            Interlocked.Read(ref _framesDropped),
            Interlocked.Read(ref _framesRepeated),
            _useBuffer ? "buffered" : "direct");
    }

    public void Dispose()
    {
        _pacerCts?.Cancel();
        try
        {
            _pacerTask?.Wait();
        }
        catch (AggregateException ex) when (ex.InnerException is OperationCanceledException)
        {
        }

        lock (_gate)
        {
            while (_bufferQueue.Count > 0)
            {
                _bufferQueue.Dequeue().Dispose();
            }
        }

        _lastDelivered?.Dispose();
        _pacerCts?.Dispose();
    }
}
