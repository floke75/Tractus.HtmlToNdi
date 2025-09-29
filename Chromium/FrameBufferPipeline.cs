using System;
using System.Buffers;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using NewTek;
using Serilog;

namespace Tractus.HtmlToNdi.Chromium;

internal sealed class FrameBufferPipeline : IDisposable
{
    private readonly int width;
    private readonly int height;
    private readonly int stride;
    private readonly int frameRate;
    private readonly Channel<VideoFrame> channel;
    private readonly CancellationTokenSource cancellationTokenSource = new();
    private readonly Task pacerTask;
    private VideoFrame? lastFrame;
    private long droppedFrameCount;

    public FrameBufferPipeline(int width, int height, int frameRate, int capacity)
    {
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height));
        }

        if (frameRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(frameRate));
        }

        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        this.width = width;
        this.height = height;
        this.stride = width * 4;
        this.frameRate = frameRate;
        this.channel = Channel.CreateBounded<VideoFrame>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });

        this.pacerTask = Task.Factory.StartNew(
            this.PacerLoopAsync,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();
    }

    public void Enqueue(nint bufferHandle)
    {
        if (this.cancellationTokenSource.IsCancellationRequested)
        {
            return;
        }

        var frame = VideoFrame.Rent(this.width, this.height, this.stride);
        try
        {
            frame.FillFrom(bufferHandle);
        }
        catch
        {
            frame.Dispose();
            throw;
        }

        if (!this.channel.Writer.TryWrite(frame))
        {
            frame.Dispose();
            Interlocked.Increment(ref this.droppedFrameCount);
        }
    }

    private async Task PacerLoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1d / this.frameRate));
        try
        {
            while (await timer.WaitForNextTickAsync(this.cancellationTokenSource.Token).ConfigureAwait(false))
            {
                if (Program.NdiSenderPtr == nint.Zero)
                {
                    continue;
                }

                VideoFrame? frameToSend = null;
                if (this.channel.Reader.TryRead(out var nextFrame))
                {
                    frameToSend = nextFrame;
                }
                else if (this.lastFrame is not null)
                {
                    frameToSend = this.lastFrame;
                }

                if (frameToSend is null)
                {
                    continue;
                }

                try
                {
                    frameToSend.SendToNdi(Program.NdiSenderPtr, this.frameRate);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Failed to send buffered frame to NDI");
                }

                if (!ReferenceEquals(frameToSend, this.lastFrame))
                {
                    this.lastFrame?.Dispose();
                    this.lastFrame = frameToSend;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown.
        }
        finally
        {
            this.channel.Writer.TryComplete();
            while (this.channel.Reader.TryRead(out var frame))
            {
                frame.Dispose();
            }

            this.lastFrame?.Dispose();
            this.lastFrame = null;

            var dropped = Interlocked.Read(ref this.droppedFrameCount);
            if (dropped > 0)
            {
                Log.Information("Buffered video pipeline dropped {Dropped} frames due to back-pressure.", dropped);
            }
        }
    }

    public void Dispose()
    {
        this.cancellationTokenSource.Cancel();
        try
        {
            this.pacerTask.GetAwaiter().GetResult();
        }
        catch
        {
            // Ignore exceptions from shutdown.
        }
        finally
        {
            this.cancellationTokenSource.Dispose();
        }
    }

    private sealed class VideoFrame : IDisposable
    {
        private static readonly ArrayPool<byte> Pool = ArrayPool<byte>.Shared;
        private byte[]? buffer;
        private readonly int width;
        private readonly int height;
        private readonly int stride;
        private readonly int length;

        private VideoFrame(byte[] buffer, int width, int height, int stride)
        {
            this.buffer = buffer;
            this.width = width;
            this.height = height;
            this.stride = stride;
            this.length = height * stride;
        }

        public static VideoFrame Rent(int width, int height, int stride)
        {
            var buffer = Pool.Rent(height * stride);
            return new VideoFrame(buffer, width, height, stride);
        }

        public void FillFrom(nint source)
        {
            if (this.buffer is null)
            {
                throw new ObjectDisposedException(nameof(VideoFrame));
            }

            System.Runtime.InteropServices.Marshal.Copy(source, this.buffer, 0, this.length);
        }

        public unsafe void SendToNdi(nint senderPtr, int frameRate)
        {
            if (this.buffer is null)
            {
                throw new ObjectDisposedException(nameof(VideoFrame));
            }

            fixed (byte* ptr = this.buffer)
            {
                var videoFrame = new NDIlib.video_frame_v2_t
                {
                    FourCC = NDIlib.FourCC_type_e.FourCC_type_BGRA,
                    frame_rate_N = frameRate,
                    frame_rate_D = 1,
                    frame_format_type = NDIlib.frame_format_type_e.frame_format_type_progressive,
                    line_stride_in_bytes = this.stride,
                    picture_aspect_ratio = (float)this.width / this.height,
                    p_data = (nint)ptr,
                    timecode = NDIlib.send_timecode_synthesize,
                    xres = this.width,
                    yres = this.height,
                };

                NDIlib.send_send_video_v2(senderPtr, ref videoFrame);
            }
        }

        public void Dispose()
        {
            if (this.buffer is not null)
            {
                Pool.Return(this.buffer);
                this.buffer = null;
            }
        }
    }
}
