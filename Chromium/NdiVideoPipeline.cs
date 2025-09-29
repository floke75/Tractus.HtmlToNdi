using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using CefSharp;
using NewTek;
using NewTek.NDI;
using Serilog;

namespace Tractus.HtmlToNdi.Chromium;

internal sealed class NdiVideoPipeline : IDisposable
{
    private readonly NdiVideoPipelineOptions options;
    private readonly Func<nint> senderAccessor;
    private readonly ArrayPool<byte> pool = ArrayPool<byte>.Shared;
    private readonly VideoFrameBuffer? buffer;
    private readonly CancellationTokenSource? pacerCancellation;
    private readonly Task? pacerTask;
    private readonly int frameRateNumerator;
    private readonly int frameRateDenominator;
    private PooledVideoFrame? lastFrame;

    public NdiVideoPipeline(NdiVideoPipelineOptions options, Func<nint> senderAccessor)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.senderAccessor = senderAccessor ?? throw new ArgumentNullException(nameof(senderAccessor));
        (this.frameRateNumerator, this.frameRateDenominator) = CalculateFrameRate(options.TargetFrameRate);

        if (this.options.EnableBuffering)
        {
            this.buffer = new VideoFrameBuffer(Math.Max(1, this.options.BufferDepth));
            this.pacerCancellation = new CancellationTokenSource();
            this.pacerTask = Task.Run(() => this.RunPacerAsync(this.pacerCancellation.Token));
        }
    }

    public void ProcessPaint(OnPaintEventArgs e)
    {
        if (e is null)
        {
            throw new ArgumentNullException(nameof(e));
        }

        var senderPtr = this.senderAccessor();
        if (senderPtr == nint.Zero)
        {
            return;
        }

        if (!this.options.EnableBuffering)
        {
            this.SendDirect(senderPtr, e.BufferHandle, e.Width, e.Height);
            return;
        }

        var stride = e.Width * 4;
        var frameLength = stride * e.Height;
        var frame = PooledVideoFrame.Rent(this.pool, e.BufferHandle, frameLength, e.Width, e.Height, stride);

        if (!this.buffer!.TryEnqueue(frame))
        {
            frame.Dispose();
        }
    }

    private void SendDirect(nint senderPtr, IntPtr bufferHandle, int width, int height)
    {
        var videoFrame = new NDIlib.video_frame_v2_t
        {
            FourCC = NDIlib.FourCC_type_e.FourCC_type_BGRA,
            frame_rate_N = this.frameRateNumerator,
            frame_rate_D = this.frameRateDenominator,
            frame_format_type = NDIlib.frame_format_type_e.frame_format_type_progressive,
            line_stride_in_bytes = width * 4,
            picture_aspect_ratio = (float)width / height,
            p_data = bufferHandle,
            timecode = NDIlib.send_timecode_synthesize,
            xres = width,
            yres = height,
        };

        NDIlib.send_send_video_v2(senderPtr, ref videoFrame);
    }

    private async Task RunPacerAsync(CancellationToken cancellationToken)
    {
        if (this.buffer is null)
        {
            return;
        }

        var interval = TimeSpan.FromSeconds(1d / this.options.TargetFrameRate);
        using var timer = new PeriodicTimer(interval);

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                var senderPtr = this.senderAccessor();
                if (senderPtr == nint.Zero)
                {
                    continue;
                }

                var nextFrame = this.buffer.DequeueLatest();
                if (nextFrame is not null)
                {
                    this.ReplaceLastFrame(nextFrame);
                }

                if (this.lastFrame is null)
                {
                    continue;
                }

                this.SendBufferedFrame(senderPtr, this.lastFrame);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when shutting down.
        }
    }

    private void SendBufferedFrame(nint senderPtr, PooledVideoFrame frame)
    {
        var bufferSpan = frame.Buffer;
        if (bufferSpan.IsEmpty)
        {
            return;
        }

        unsafe
        {
            fixed (byte* bufferPtr = bufferSpan)
            {
                var videoFrame = new NDIlib.video_frame_v2_t
                {
                    FourCC = NDIlib.FourCC_type_e.FourCC_type_BGRA,
                    frame_rate_N = this.frameRateNumerator,
                    frame_rate_D = this.frameRateDenominator,
                    frame_format_type = NDIlib.frame_format_type_e.frame_format_type_progressive,
                    line_stride_in_bytes = frame.Stride,
                    picture_aspect_ratio = (float)frame.Width / frame.Height,
                    p_data = (IntPtr)bufferPtr,
                    timecode = NDIlib.send_timecode_synthesize,
                    xres = frame.Width,
                    yres = frame.Height,
                };

                NDIlib.send_send_video_v2(senderPtr, ref videoFrame);
            }
        }
    }

    private void ReplaceLastFrame(PooledVideoFrame newFrame)
    {
        var oldFrame = Interlocked.Exchange(ref this.lastFrame, newFrame);
        oldFrame?.Dispose();
    }

    public void Dispose()
    {
        this.pacerCancellation?.Cancel();
        try
        {
            this.pacerTask?.Wait();
        }
        catch (AggregateException ex) when (ex.InnerException is OperationCanceledException)
        {
        }

        this.pacerCancellation?.Dispose();

        this.lastFrame?.Dispose();
        this.lastFrame = null;

        this.buffer?.Dispose();
    }

    private static (int Numerator, int Denominator) CalculateFrameRate(double targetFps)
    {
        Span<(double fps, int n, int d)> knownRates = stackalloc (double, int, int)[]
        {
            (23.976, 24000, 1001),
            (29.97, 30000, 1001),
            (47.952, 48000, 1001),
            (59.94, 60000, 1001),
        };

        foreach (var known in knownRates)
        {
            if (Math.Abs(targetFps - known.fps) <= 0.02)
            {
                return (known.n, known.d);
            }
        }

        var rounded = (int)Math.Round(targetFps);
        if (rounded <= 0)
        {
            rounded = 1;
        }

        return (rounded, 1);
    }
}

internal sealed class NdiVideoPipelineOptions
{
    public double TargetFrameRate { get; init; } = 60d;

    public bool EnableBuffering { get; init; }

    public int BufferDepth { get; init; } = 3;
}

internal sealed class VideoFrameBuffer : IDisposable
{
    private readonly Queue<PooledVideoFrame> queue = new();
    private readonly object gate = new();
    private readonly int capacity;
    private bool disposed;

    public VideoFrameBuffer(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        this.capacity = capacity;
    }

    public bool TryEnqueue(PooledVideoFrame frame)
    {
        if (frame is null)
        {
            throw new ArgumentNullException(nameof(frame));
        }

        PooledVideoFrame? droppedFrame = null;

        lock (this.gate)
        {
            if (this.disposed)
            {
                return false;
            }

            if (this.queue.Count >= this.capacity)
            {
                droppedFrame = this.queue.Dequeue();
            }

            this.queue.Enqueue(frame);
        }

        if (droppedFrame is not null)
        {
            droppedFrame.Dispose();
            Log.Logger.Debug("Dropped a buffered frame because the ring buffer is full");
        }

        return true;
    }

    public PooledVideoFrame? DequeueLatest()
    {
        lock (this.gate)
        {
            if (this.disposed || this.queue.Count == 0)
            {
                return null;
            }

            PooledVideoFrame? latest = null;
            while (this.queue.Count > 0)
            {
                var candidate = this.queue.Dequeue();
                latest?.Dispose();
                latest = candidate;
            }

            return latest;
        }
    }

    public void Dispose()
    {
        lock (this.gate)
        {
            if (this.disposed)
            {
                return;
            }

            while (this.queue.Count > 0)
            {
                this.queue.Dequeue().Dispose();
            }

            this.disposed = true;
        }
    }
}

internal sealed class PooledVideoFrame : IDisposable
{
    private readonly ArrayPool<byte> pool;
    private byte[]? buffer;

    private PooledVideoFrame(ArrayPool<byte> pool, byte[] buffer, int length, int width, int height, int stride)
    {
        this.pool = pool;
        this.buffer = buffer;
        this.Length = length;
        this.Width = width;
        this.Height = height;
        this.Stride = stride;
    }

    public int Length { get; }

    public int Width { get; }

    public int Height { get; }

    public int Stride { get; }

    public Span<byte> Buffer => this.buffer?.AsSpan(0, this.Length) ?? Span<byte>.Empty;

    public static PooledVideoFrame Rent(ArrayPool<byte> pool, IntPtr source, int length, int width, int height, int stride)
    {
        var buffer = pool.Rent(length);
        Marshal.Copy(source, buffer, 0, length);
        return new PooledVideoFrame(pool, buffer, length, width, height, stride);
    }

    public void Dispose()
    {
        if (this.buffer is null)
        {
            return;
        }

        this.pool.Return(this.buffer);
        this.buffer = null;
    }
}
