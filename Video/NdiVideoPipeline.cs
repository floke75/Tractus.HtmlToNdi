using CefSharp.OffScreen;
using NewTek.NDI;
using Serilog;

namespace Tractus.HtmlToNdi.Video;

public sealed class NdiVideoPipeline : IFrameConsumer, IDisposable
{
    private readonly Func<nint> _ndiHandleAccessor;
    private readonly NdiVideoPipelineOptions _options;
    private readonly ILogger _logger;
    private readonly FrameTimeAverager _outputAverager = new();
    private readonly NdiPipelineTelemetry _telemetry = new();
    private readonly object _sync = new();
    private FrameRingBuffer? _buffer;
    private FramePacer? _pacer;
    private BufferedVideoFrame? _lastFrame;
    private bool _started;

    public NdiVideoPipeline(Func<nint> ndiHandleAccessor, NdiVideoPipelineOptions options, ILogger logger)
    {
        _ndiHandleAccessor = ndiHandleAccessor;
        _options = options;
        _logger = logger;

        if (_options.EnableBufferedOutput)
        {
            _buffer = new FrameRingBuffer(Math.Max(1, _options.BufferDepth));
        }
    }

    public NdiPipelineTelemetry Telemetry => _telemetry;

    public bool IsBuffered => _options.EnableBufferedOutput;

    public FrameRingBuffer? Buffer => _buffer;

    public void Start(FrameRate pacerRate)
    {
        if (!_options.EnableBufferedOutput)
        {
            _started = true;
            return;
        }

        if (_buffer == null)
        {
            throw new InvalidOperationException("Buffer not configured.");
        }

        if (_pacer != null)
        {
            return;
        }

        _pacer = new FramePacer(pacerRate, _buffer, this, _logger);
        _pacer.Start();
        _started = true;
    }

    public async Task StopAsync()
    {
        if (_pacer != null)
        {
            await _pacer.StopAsync().ConfigureAwait(false);
        }

        lock (_sync)
        {
            _pacer = null;
            _lastFrame?.Dispose();
            _lastFrame = null;
            _buffer?.Clear();
            _started = false;
        }
    }

    public void HandlePaint(OnPaintEventArgs e)
    {
        _telemetry.IncrementCaptured();
        if (!_options.EnableBufferedOutput)
        {
            SendDirect(e.BufferHandle, e.Width, e.Height, e.Stride, repeated: false);
            return;
        }

        if (_buffer == null)
        {
            return;
        }

        var frame = BufferedVideoFrame.Rent(e.Width, e.Height, e.Stride);
        frame.CopyFrom(e.BufferHandle, e.Stride * e.Height);
        var dropped = _buffer.Enqueue(frame);
        if (dropped != null)
        {
            _telemetry.IncrementOverflow();
            dropped.Dispose();
        }
    }

    public void OnFrame(BufferedVideoFrame? frame, FramePacerDecision decision, int discarded)
    {
        lock (_sync)
        {
            switch (decision)
            {
                case FramePacerDecision.Fresh:
                    if (frame == null)
                    {
                        return;
                    }

                    _telemetry.AddDropped(discarded);
                    _lastFrame?.Dispose();
                    _lastFrame = frame;
                    SendBufferedFrame(frame, repeated: false);
                    break;
                case FramePacerDecision.RepeatLast:
                    if (_lastFrame != null)
                    {
                        SendBufferedFrame(_lastFrame, repeated: true);
                    }
                    else
                    {
                        _telemetry.IncrementUnderrun();
                    }
                    break;
            }
        }
    }

    private void SendBufferedFrame(BufferedVideoFrame frame, bool repeated)
    {
        unsafe
        {
            fixed (byte* ptr = frame.Buffer)
            {
                SendFrame((nint)ptr, frame.Width, frame.Height, frame.Stride, repeated);
            }
        }
    }

    private void SendDirect(nint bufferHandle, int width, int height, int stride, bool repeated)
    {
        SendFrame(bufferHandle, width, height, stride, repeated);
    }

    private void SendFrame(nint dataPtr, int width, int height, int stride, bool repeated)
    {
        var sender = _ndiHandleAccessor();
        if (sender == nint.Zero)
        {
            return;
        }

        var frameRate = _outputAverager.GetFrameRateOr(_options.NdiFrameRate);
        var videoFrame = new NDIlib.video_frame_v2_t
        {
            xres = width,
            yres = height,
            line_stride_in_bytes = stride,
            FourCC = NDIlib.FourCC_type_e.FourCC_type_BGRA,
            frame_rate_N = frameRate.Numerator,
            frame_rate_D = frameRate.Denominator,
            frame_format_type = NDIlib.frame_format_type_e.frame_format_type_progressive,
            picture_aspect_ratio = width / (float)height,
            timecode = NDIlib.send_timecode_synthesize,
            p_data = dataPtr,
        };

        NDIlib.send_send_video_v2(sender, ref videoFrame);
        _telemetry.IncrementSent();
        if (repeated)
        {
            _telemetry.IncrementRepeated();
        }

        _outputAverager.RecordFrame(DateTime.UtcNow);
    }

    public void Dispose()
    {
        _ = StopAsync();
    }
}
