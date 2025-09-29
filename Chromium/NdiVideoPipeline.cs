using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
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
    private readonly VideoFrameBuffer? buffer;
    private readonly CancellationTokenSource? pacerCancellation;
    private readonly Thread? pacerThread;
    private readonly int frameRateNumerator;
    private readonly int frameRateDenominator;
    private PooledVideoFrame? lastFrame;

    // Telemetry fields from PR #20
    private long sentFrames;
    private long repeatedFrames;
    private long droppedFrames;
    private double minIntervalMs = double.MaxValue;
    private double maxIntervalMs;
    private double accumulatedIntervalMs;
    private DateTime lastLogUtc = DateTime.MinValue;

    public NdiVideoPipeline(NdiVideoPipelineOptions options, Func<nint> senderAccessor)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.senderAccessor = senderAccessor ?? throw new ArgumentNullException(nameof(senderAccessor));
        (this.frameRateNumerator, this.frameRateDenominator) = CalculateFrameRate(options.TargetFrameRate);

        if (this.options.EnableBuffering)
        {
            this.buffer = new VideoFrameBuffer(Math.Max(1, this.options.BufferDepth));
            this.pacerCancellation = new CancellationTokenSource();
            this.pacerThread = new Thread(this.RunPacer)
            {
                Name = "NdiFramePacer",
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal
            };
            this.pacerThread.Start();
        }
    }

    public void ProcessPaint(OnPaintEventArgs e)
    {
        if (e is null) throw new ArgumentNullException(nameof(e));

        var senderPtr = this.senderAccessor();
        if (senderPtr == nint.Zero) return;

        if (!this.options.EnableBuffering)
        {
            this.SendDirect(senderPtr, e.BufferHandle, e.Width, e.Height);
            return;
        }

        var stride = e.Width * 4;
        var frameLength = stride * e.Height;
        var frame = PooledVideoFrame.Rent(e.BufferHandle, frameLength, e.Width, e.Height, stride);

        // Enqueue gives the buffer ownership of one reference.
        var droppedFrame = this.buffer!.Enqueue(frame);
        // If the buffer was full, it returns the oldest frame, which we must now dispose.
        droppedFrame?.Dispose();
        // We have handed off our reference to the buffer, so we must dispose our local reference.
        frame.Dispose();
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

    private void RunPacer()
    {
        if (this.buffer is null || this.pacerCancellation is null) return;

        var token = this.pacerCancellation.Token;
        var interval = TimeSpan.FromSeconds(1d / this.options.TargetFrameRate);
        var stopwatch = Stopwatch.StartNew();
        var nextDeadline = stopwatch.Elapsed;
        var lastTickUtc = DateTime.MinValue;
        long lastReadSequence = -1;

        while (!token.IsCancellationRequested)
        {
            nextDeadline += interval;
            var delay = nextDeadline - stopwatch.Elapsed;
            if (delay > TimeSpan.Zero)
            {
                try
                {
                    token.WaitHandle.WaitOne(delay);
                }
                catch (OperationCanceledException) { break; }
            }
            if(token.IsCancellationRequested) break;

            var senderPtr = this.senderAccessor();
            if (senderPtr == nint.Zero) continue;

            var utcNow = DateTime.UtcNow;
            var actualInterval = lastTickUtc == DateTime.MinValue ? interval : utcNow - lastTickUtc;
            lastTickUtc = utcNow;

            var readResult = this.buffer.ReadLatest(ref lastReadSequence);
            var isRepeat = false;

            if (readResult.Frame is not null)
            {
                // We received a new frame, so we release our hold on the previous one.
                this.lastFrame?.Dispose();
                this.lastFrame = readResult.Frame; // The buffer gives us ownership of one reference.
            }
            else if (this.lastFrame is not null)
            {
                isRepeat = true;
            }

            if (this.lastFrame is null) continue;

            this.SendBufferedFrame(senderPtr, this.lastFrame);
            this.UpdateMetrics(actualInterval, isRepeat, readResult.DroppedCount);
            this.LogMetricsIfNeeded(utcNow);
        }
    }

    private void UpdateMetrics(TimeSpan interval, bool isRepeat, int dropped)
    {
        this.sentFrames++;
        this.droppedFrames += dropped;
        if(isRepeat) this.repeatedFrames++;

        var intervalMs = interval.TotalMilliseconds;
        this.accumulatedIntervalMs += intervalMs;
        this.minIntervalMs = Math.Min(this.minIntervalMs, intervalMs);
        this.maxIntervalMs = Math.Max(this.maxIntervalMs, intervalMs);
    }

    private void LogMetricsIfNeeded(DateTime utcNow)
    {
        var logInterval = TimeSpan.FromSeconds(5);
        if (this.lastLogUtc != DateTime.MinValue && utcNow - this.lastLogUtc < logInterval) return;
        if (this.sentFrames == 0) return;

        var avg = this.accumulatedIntervalMs / this.sentFrames;
        Log.Information(
            "Frame pacer stats: sent={Sent} repeat={Repeat} dropped={Dropped} interval_avg={Avg:F3}ms interval_min={Min:F3}ms interval_max={Max:F3}ms target={Target:F3}ms buffer_depth={BufferDepth}",
            this.sentFrames, this.repeatedFrames, this.droppedFrames, avg, this.minIntervalMs, this.maxIntervalMs,
            TimeSpan.FromSeconds(1d / this.options.TargetFrameRate).TotalMilliseconds, this.buffer.Capacity);

        this.lastLogUtc = utcNow;
        this.sentFrames = 0;
        this.repeatedFrames = 0;
        this.droppedFrames = 0;
        this.accumulatedIntervalMs = 0;
        this.minIntervalMs = double.MaxValue;
        this.maxIntervalMs = 0;
    }

    private void SendBufferedFrame(nint senderPtr, PooledVideoFrame frame)
    {
        var bufferSpan = frame.Buffer;
        if (bufferSpan.IsEmpty) return;

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

    public void Dispose()
    {
        this.pacerCancellation?.Cancel();
        this.pacerThread?.Join();
        this.pacerCancellation?.Dispose();
        this.lastFrame?.Dispose();
        this.lastFrame = null;
        this.buffer?.Dispose();
    }

    private static (int Numerator, int Denominator) CalculateFrameRate(double targetFps)
    {
        Span<(double fps, int n, int d)> knownRates = stackalloc (double, int, int)[]
        {
            (23.976, 24000, 1001), (29.97, 30000, 1001),
            (47.952, 48000, 1001), (59.94, 60000, 1001),
        };
        foreach (var known in knownRates)
        {
            if (Math.Abs(targetFps - known.fps) <= 0.02) return (known.n, known.d);
        }
        var rounded = (int)Math.Round(targetFps);
        return (rounded > 0 ? rounded : 1, 1);
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
    private readonly PooledVideoFrame?[] slots;
    private long writeSequence = -1;
    private long publishedSequence = -1;

    public int Capacity { get; }

    public VideoFrameBuffer(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        this.Capacity = capacity;
        this.slots = new PooledVideoFrame[capacity];
    }

    public PooledVideoFrame? Enqueue(PooledVideoFrame frame)
    {
        frame.AddRef();
        var sequence = Interlocked.Increment(ref this.writeSequence);
        var index = (int)(sequence % this.Capacity);
        var oldFrame = Interlocked.Exchange(ref this.slots[index], frame);
        Volatile.Write(ref this.publishedSequence, sequence);
        return oldFrame;
    }

    public FrameReadResult ReadLatest(ref long lastReadSequence)
    {
        var latestSequence = Volatile.Read(ref this.publishedSequence);
        if (latestSequence < 0 || latestSequence == lastReadSequence)
        {
            return FrameReadResult.Empty;
        }

        var index = (int)(latestSequence % this.Capacity);
        var frame = Volatile.Read(ref this.slots[index]);

        if (frame is null) return FrameReadResult.Empty;

        frame.AddRef();

        var dropped = (int)Math.Max(0, latestSequence - lastReadSequence - 1);
        lastReadSequence = latestSequence;
        return new FrameReadResult(frame, latestSequence, dropped);
    }

    public void Dispose()
    {
        foreach (var frame in this.slots)
        {
            frame?.Dispose();
        }
    }

    internal readonly struct FrameReadResult
    {
        public static FrameReadResult Empty => new(null, -1, 0);
        public PooledVideoFrame? Frame { get; }
        public long Sequence { get; }
        public int DroppedCount { get; }
        public FrameReadResult(PooledVideoFrame? frame, long sequence, int dropped)
        {
            this.Frame = frame;
            this.Sequence = sequence;
            this.DroppedCount = dropped;
        }
    }
}

internal sealed class PooledVideoFrame : IDisposable
{
    private readonly ArrayPool<byte> pool;
    private byte[]? buffer;
    private int refCount;

    private PooledVideoFrame(ArrayPool<byte> pool, byte[] buffer, int length, int width, int height, int stride)
    {
        this.pool = pool;
        this.buffer = buffer;
        this.Length = length;
        this.Width = width;
        this.Height = height;
        this.Stride = stride;
        this.refCount = 1;
    }

    public int Length { get; }
    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public Span<byte> Buffer => this.buffer?.AsSpan(0, this.Length) ?? Span<byte>.Empty;

    public static PooledVideoFrame Rent(IntPtr source, int length, int width, int height, int stride)
    {
        var pool = ArrayPool<byte>.Shared;
        var buffer = pool.Rent(length);
        Marshal.Copy(source, buffer, 0, length);
        return new PooledVideoFrame(pool, buffer, length, width, height, stride);
    }

    public void AddRef()
    {
        Interlocked.Increment(ref this.refCount);
    }

    public void Dispose()
    {
        if (Interlocked.Decrement(ref this.refCount) == 0)
        {
            if (this.buffer is null) return;
            this.pool.Return(this.buffer);
            this.buffer = null;
        }
    }
}