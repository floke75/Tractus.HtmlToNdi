using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using CefSharp;
using CefSharp.OffScreen;
using Serilog;

namespace Tractus.HtmlToNdi.Chromium;

internal sealed class FramePump : IDisposable
{
    private readonly ChromiumWebBrowser browser;
    private readonly double frameRate;
    private readonly CancellationTokenSource cancellation = new();
    private readonly Task pumpTask;

    public FramePump(ChromiumWebBrowser browser, double frameRate)
    {
        this.browser = browser;
        this.frameRate = frameRate <= 0 ? 60 : frameRate;
        this.pumpTask = Task.Run(this.RunAsync);
    }

    private async Task RunAsync()
    {
        var period = TimeSpan.FromSeconds(1.0 / this.frameRate);
        var stopwatch = Stopwatch.StartNew();
        var nextTick = period;

        try
        {
            while (!this.cancellation.IsCancellationRequested)
            {
                var delay = nextTick - stopwatch.Elapsed;
                while (delay < TimeSpan.Zero)
                {
                    nextTick += period;
                    delay = nextTick - stopwatch.Elapsed;
                }

                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, this.cancellation.Token).ConfigureAwait(false);
                }

                this.browser.GetBrowserHost()?.Invalidate(PaintElementType.View);
                nextTick += period;
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void Dispose()
    {
        this.cancellation.Cancel();
        try
        {
            this.pumpTask.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(e => e is OperationCanceledException || e is TaskCanceledException))
        {
        }
        finally
        {
            this.cancellation.Dispose();
        }
    }
}

internal sealed class FrameRingBuffer
{
    private readonly VideoFrame?[] frames;
    private readonly object gate = new();
    private int head;
    private int tail;
    private int count;

    public FrameRingBuffer(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        this.frames = new VideoFrame[capacity];
    }

    public VideoFrame? Enqueue(VideoFrame frame)
    {
        VideoFrame? dropped = null;

        lock (this.gate)
        {
            if (this.count == this.frames.Length)
            {
                dropped = this.frames[this.head];
                this.frames[this.head] = null;
                this.head = (this.head + 1) % this.frames.Length;
                this.count--;
            }

            this.frames[this.tail] = frame;
            this.tail = (this.tail + 1) % this.frames.Length;
            this.count++;
        }

        return dropped;
    }

    public bool TryDequeue(out VideoFrame frame)
    {
        lock (this.gate)
        {
            if (this.count == 0)
            {
                frame = null!;
                return false;
            }

            var next = this.frames[this.head]!;
            this.frames[this.head] = null;
            this.head = (this.head + 1) % this.frames.Length;
            this.count--;
            frame = next;
            return true;
        }
    }

    public IReadOnlyList<VideoFrame> Drain()
    {
        var drained = new List<VideoFrame>();

        lock (this.gate)
        {
            while (this.count > 0)
            {
                var frame = this.frames[this.head];
                if (frame is not null)
                {
                    drained.Add(frame);
                }

                this.frames[this.head] = null;
                this.head = (this.head + 1) % this.frames.Length;
                this.count--;
            }
        }

        return drained;
    }
}

internal sealed class VideoFramePool
{
    public VideoFrame Rent(int width, int height, int stride)
    {
        if (width <= 0 || height <= 0 || stride <= 0)
        {
            throw new ArgumentOutOfRangeException("A frame requires positive width, height, and stride.");
        }

        var buffer = ArrayPool<byte>.Shared.Rent(stride * height);
        return new VideoFrame(this, buffer);
    }

    internal void Return(byte[] buffer)
    {
        ArrayPool<byte>.Shared.Return(buffer);
    }
}

internal sealed class VideoFrame
{
    private readonly VideoFramePool owner;
    private readonly object gate = new();
    private byte[]? buffer;
    private GCHandle handle;
    private int referenceCount;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public int Stride { get; private set; }

    public nint Pointer
    {
        get
        {
            if (this.buffer is null)
            {
                return nint.Zero;
            }

            return this.handle.AddrOfPinnedObject();
        }
    }

    internal VideoFrame(VideoFramePool owner, byte[] buffer)
    {
        this.owner = owner;
        this.buffer = buffer;
        this.handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        this.referenceCount = 1;
    }

    public void CopyFrom(nint source, int stride, int width, int height)
    {
        if (this.buffer is null)
        {
            throw new ObjectDisposedException(nameof(VideoFrame));
        }

        var totalBytes = stride * height;
        if (totalBytes > this.buffer.Length)
        {
            throw new ArgumentException("Source frame is larger than the pooled buffer.");
        }

        unsafe
        {
            Buffer.MemoryCopy((void*)source, (void*)this.Pointer, this.buffer.Length, totalBytes);
        }

        this.Width = width;
        this.Height = height;
        this.Stride = stride;
    }

    public void Release()
    {
        bool dispose = false;

        lock (this.gate)
        {
            if (this.referenceCount <= 0)
            {
                return;
            }

            this.referenceCount--;
            dispose = this.referenceCount == 0;
        }

        if (dispose)
        {
            this.Dispose();
        }
    }

    public void AddRef()
    {
        lock (this.gate)
        {
            if (this.referenceCount <= 0)
            {
                throw new ObjectDisposedException(nameof(VideoFrame));
            }

            this.referenceCount++;
        }
    }

    private void Dispose()
    {
        if (this.buffer is null)
        {
            return;
        }

        if (this.handle.IsAllocated)
        {
            this.handle.Free();
        }

        this.owner.Return(this.buffer);
        this.buffer = null;
    }
}

internal sealed class FramePacer : IDisposable
{
    private readonly FrameRingBuffer buffer;
    private readonly double frameRate;
    private readonly Action<VideoFrame> consumer;
    private readonly CancellationTokenSource cancellation = new();
    private readonly Task pacingTask;

    public FramePacer(FrameRingBuffer buffer, double frameRate, Action<VideoFrame> consumer)
    {
        this.buffer = buffer;
        this.frameRate = frameRate <= 0 ? 60 : frameRate;
        this.consumer = consumer;
        this.pacingTask = Task.Run(this.RunAsync);
    }

    private async Task RunAsync()
    {
        var period = TimeSpan.FromSeconds(1.0 / this.frameRate);
        var stopwatch = Stopwatch.StartNew();
        var nextTick = period;
        VideoFrame? lastFrame = null;

        try
        {
            while (!this.cancellation.IsCancellationRequested)
            {
                if (this.buffer.TryDequeue(out var frame))
                {
                    lastFrame?.Release();
                    lastFrame = frame;
                }

                if (lastFrame is not null)
                {
                    try
                    {
                        this.consumer(lastFrame);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "NDI frame send failed inside the frame pacer.");
                    }
                }

                var delay = nextTick - stopwatch.Elapsed;
                while (delay < TimeSpan.Zero)
                {
                    nextTick += period;
                    delay = nextTick - stopwatch.Elapsed;
                }

                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, this.cancellation.Token).ConfigureAwait(false);
                }

                nextTick += period;
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            lastFrame?.Release();
        }
    }

    public void Dispose()
    {
        this.cancellation.Cancel();
        try
        {
            this.pacingTask.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(e => e is OperationCanceledException || e is TaskCanceledException))
        {
        }
        finally
        {
            this.cancellation.Dispose();
        }
    }
}
