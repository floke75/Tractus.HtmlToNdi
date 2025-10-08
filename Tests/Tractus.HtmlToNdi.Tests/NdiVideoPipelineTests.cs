using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Serilog.Core;
using Tractus.HtmlToNdi.Video;
using Xunit;
using NewTek;
using NewTek.NDI;

namespace Tractus.HtmlToNdi.Tests;

public class NdiVideoPipelineTests
{
    private sealed record SentFrame(NDIlib.video_frame_v2_t Frame, byte[] Payload);

    private sealed class CollectingSender : INdiVideoSender
    {
        private readonly object gate = new();
        private readonly List<SentFrame> frames = new();

        public IReadOnlyList<SentFrame> Frames
        {
            get
            {
                lock (gate)
                {
                    return frames.ToList();
                }
            }
        }

        public void Send(ref NDIlib.video_frame_v2_t frame)
        {
            lock (gate)
            {
                var size = frame.yres * frame.line_stride_in_bytes;
                var payload = new byte[size];
                if (size > 0 && frame.p_data != IntPtr.Zero)
                {
                    Marshal.Copy(frame.p_data, payload, 0, size);
                }

                frames.Add(new SentFrame(frame, payload));
            }
        }
    }

    private static ILogger CreateNullLogger() => new LoggerConfiguration().WriteTo.Sink(new NullSink()).CreateLogger();

    [Fact]
    public void DirectModeSendsImmediately()
    {
        var sender = new CollectingSender();
        var options = new NdiVideoPipelineOptions
        {
            EnableBuffering = false,
            TelemetryInterval = TimeSpan.FromDays(1)
        };

        var pipeline = new NdiVideoPipeline(sender, new FrameRate(60, 1), options, CreateNullLogger());

        var size = 4 * 2 * 2;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            var frame = new CapturedFrame(buffer, 2, 2, 8);
            pipeline.HandleFrame(frame);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            pipeline.Dispose();
        }

        var frames = sender.Frames;
        Assert.Single(frames);
        Assert.Equal(60, frames[0].Frame.frame_rate_N);
        Assert.Equal(1, frames[0].Frame.frame_rate_D);
    }

    [Fact]
    public async Task BufferedModeWaitsForWarmupBeforeSending()
    {
        var sender = new CollectingSender();
        var options = new NdiVideoPipelineOptions
        {
            EnableBuffering = true,
            BufferDepth = 3,
            TelemetryInterval = TimeSpan.FromDays(1)
        };

        var pipeline = new NdiVideoPipeline(sender, new FrameRate(30, 1), options, CreateNullLogger());
        pipeline.Start();

        var frameSize = 4 * 2 * 2;
        var buffers = new IntPtr[3];
        try
        {
            await Task.Delay(150);
            Assert.Empty(sender.Frames);

            for (var i = 0; i < buffers.Length; i++)
            {
                buffers[i] = Marshal.AllocHGlobal(frameSize);
                FillBuffer(buffers[i], frameSize, (byte)(0x10 + i));
                pipeline.HandleFrame(new CapturedFrame(buffers[i], 2, 2, 8));
            }

            var warmed = SpinWait.SpinUntil(() => sender.Frames.Count >= buffers.Length, TimeSpan.FromMilliseconds(600));
            Assert.True(warmed);
            Assert.True(pipeline.BufferPrimed);

            var frames = sender.Frames.Take(buffers.Length).ToArray();
            Assert.Equal(0x10, frames[0].Payload[0]);
            Assert.Equal(0x11, frames[1].Payload[0]);
            Assert.Equal(0x12, frames[2].Payload[0]);
        }
        finally
        {
            pipeline.Dispose();
            foreach (var ptr in buffers)
            {
                if (ptr != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(ptr);
                }
            }
        }
    }

    [Fact]
    public async Task BufferedModeRepeatsLastFrameWhenIdle()
    {
        var sender = new CollectingSender();
        var options = new NdiVideoPipelineOptions
        {
            EnableBuffering = true,
            BufferDepth = 2,
            TelemetryInterval = TimeSpan.FromDays(1)
        };

        var pipeline = new NdiVideoPipeline(sender, new FrameRate(30, 1), options, CreateNullLogger());
        pipeline.Start();

        var frameSize = 4 * 2 * 2;
        var buffers = new IntPtr[2];
        try
        {
            buffers[0] = Marshal.AllocHGlobal(frameSize);
            buffers[1] = Marshal.AllocHGlobal(frameSize);
            FillBuffer(buffers[0], frameSize, 0x20);
            FillBuffer(buffers[1], frameSize, 0x30);

            pipeline.HandleFrame(new CapturedFrame(buffers[0], 2, 2, 8));
            pipeline.HandleFrame(new CapturedFrame(buffers[1], 2, 2, 8));

            var primed = SpinWait.SpinUntil(() => sender.Frames.Count >= 2, TimeSpan.FromMilliseconds(500));
            Assert.True(primed);

            await Task.Delay(400);

            var frames = sender.Frames;
            Assert.True(frames.Count >= 4, "Expected repeated frames while idle");
            var last = frames[^1];
            var previous = frames[^2];
            Assert.Equal(previous.Frame.p_data, last.Frame.p_data);
            Assert.Equal(previous.Payload[0], last.Payload[0]);
        }
        finally
        {
            pipeline.Dispose();
            foreach (var ptr in buffers)
            {
                if (ptr != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(ptr);
                }
            }
        }
    }

    [Fact]
    public async Task BufferedModeRewarmsAfterUnderrun()
    {
        var sender = new CollectingSender();
        var options = new NdiVideoPipelineOptions
        {
            EnableBuffering = true,
            BufferDepth = 2,
            TelemetryInterval = TimeSpan.FromDays(1)
        };

        var pipeline = new NdiVideoPipeline(sender, new FrameRate(30, 1), options, CreateNullLogger());
        pipeline.Start();

        var frameSize = 4 * 2 * 2;
        var buffers = new IntPtr[5];
        try
        {
            for (var i = 0; i < 2; i++)
            {
                buffers[i] = Marshal.AllocHGlobal(frameSize);
                FillBuffer(buffers[i], frameSize, (byte)(0x40 + i));
                pipeline.HandleFrame(new CapturedFrame(buffers[i], 2, 2, 8));
            }

            var primed = SpinWait.SpinUntil(() => pipeline.BufferPrimed && sender.Frames.Count >= 2, TimeSpan.FromMilliseconds(500));
            Assert.True(primed);

            await Task.Delay(250);
            Assert.True(pipeline.BufferUnderruns >= 1);
            Assert.False(pipeline.BufferPrimed);

            for (var i = 2; i < buffers.Length; i++)
            {
                buffers[i] = Marshal.AllocHGlobal(frameSize);
                FillBuffer(buffers[i], frameSize, (byte)(0x60 + i));
                pipeline.HandleFrame(new CapturedFrame(buffers[i], 2, 2, 8));
            }

            var rearmed = SpinWait.SpinUntil(
                () => pipeline.BufferPrimed && sender.Frames.Any(f => f.Payload[0] >= 0x64),
                TimeSpan.FromMilliseconds(1500));
            Assert.True(rearmed);
            Assert.True(pipeline.LastWarmupDuration > TimeSpan.Zero);
            Assert.True(pipeline.LastWarmupRepeats > 0);
        }
        finally
        {
            pipeline.Dispose();
            foreach (var ptr in buffers)
            {
                if (ptr != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(ptr);
                }
            }
        }
    }

    [Fact]
    public async Task BufferedModeRequiresFullDepthBeforeResuming()
    {
        var sender = new CollectingSender();
        var options = new NdiVideoPipelineOptions
        {
            EnableBuffering = true,
            BufferDepth = 3,
            TelemetryInterval = TimeSpan.FromDays(1)
        };

        var pipeline = new NdiVideoPipeline(sender, new FrameRate(30, 1), options, CreateNullLogger());
        pipeline.Start();

        var frameSize = 4 * 2 * 2;
        var buffers = new IntPtr[8];
        try
        {
            for (var i = 0; i < 3; i++)
            {
                buffers[i] = Marshal.AllocHGlobal(frameSize);
                FillBuffer(buffers[i], frameSize, (byte)(0x20 + i));
                pipeline.HandleFrame(new CapturedFrame(buffers[i], 2, 2, 8));
            }

            var primed = SpinWait.SpinUntil(() => pipeline.BufferPrimed && sender.Frames.Count >= 3, TimeSpan.FromMilliseconds(800));
            Assert.True(primed);

            var underrun = SpinWait.SpinUntil(() => !pipeline.BufferPrimed && pipeline.BufferUnderruns >= 1, TimeSpan.FromMilliseconds(800));
            Assert.True(underrun);

            var baselineCount = sender.Frames.Count;

            for (var i = 3; i < 5; i++)
            {
                buffers[i] = Marshal.AllocHGlobal(frameSize);
                FillBuffer(buffers[i], frameSize, (byte)(0x80 + i));
                pipeline.HandleFrame(new CapturedFrame(buffers[i], 2, 2, 8));
            }

            await Task.Delay(250);
            Assert.False(pipeline.BufferPrimed);
            Assert.Empty(sender.Frames.Skip(baselineCount).Where(f => f.Payload[0] >= 0x80));

            for (var i = 5; i < buffers.Length; i++)
            {
                buffers[i] = Marshal.AllocHGlobal(frameSize);
                FillBuffer(buffers[i], frameSize, (byte)(0x80 + i));
                pipeline.HandleFrame(new CapturedFrame(buffers[i], 2, 2, 8));
            }

            var resumed = SpinWait.SpinUntil(
                () => pipeline.BufferPrimed && sender.Frames.Skip(baselineCount).Any(f => f.Payload[0] >= 0x80),
                TimeSpan.FromMilliseconds(2000));
            Assert.True(resumed);

            var freshFrames = sender.Frames.Where(f => f.Payload[0] >= 0x80).Select(f => f.Payload[0]).Distinct().ToArray();
            Assert.Contains((byte)0x83, freshFrames);
            Assert.Contains((byte)0x84, freshFrames);
            Assert.Contains((byte)0x85, freshFrames);
        }
        finally
        {
            pipeline.Dispose();
            foreach (var ptr in buffers)
            {
                if (ptr != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(ptr);
                }
            }
        }
    }

    private static void FillBuffer(IntPtr buffer, int size, byte value)
    {
        var data = new byte[size];
        Array.Fill(data, value);
        Marshal.Copy(data, 0, buffer, size);
    }
}

internal sealed class NullSink : ILogEventSink
{
    public void Emit(Serilog.Events.LogEvent logEvent)
    {
    }
}
