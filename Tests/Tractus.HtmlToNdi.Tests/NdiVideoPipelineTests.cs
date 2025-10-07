using System.Linq;
using System.Runtime.InteropServices;
using Serilog;
using Serilog.Core;
using Tractus.HtmlToNdi.Video;
using Xunit;
using NewTek;
using NewTek.NDI;

namespace Tractus.HtmlToNdi.Tests;

public class NdiVideoPipelineTests
{
    private sealed record SentFrame(NDIlib.video_frame_v2_t Frame, byte FirstByte);

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
                byte firstByte = 0;
                if (frame.p_data != IntPtr.Zero)
                {
                    firstByte = Marshal.ReadByte(frame.p_data);
                }

                frames.Add(new SentFrame(frame, firstByte));
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
    public async Task BufferedModeWaitsForConfiguredDepth()
    {
        var sender = new CollectingSender();
        var options = new NdiVideoPipelineOptions
        {
            EnableBuffering = true,
            BufferDepth = 2,
            TelemetryInterval = TimeSpan.FromDays(1)
        };

        using var pipeline = new NdiVideoPipeline(sender, new FrameRate(30, 1), options, CreateNullLogger());
        pipeline.Start();

        var size = 4 * 2 * 2;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            FillBuffer(buffer, size, 0x11);
            pipeline.HandleFrame(new CapturedFrame(buffer, 2, 2, 8));

            await Task.Delay(200);
            Assert.Empty(sender.Frames);

            FillBuffer(buffer, size, 0x22);
            pipeline.HandleFrame(new CapturedFrame(buffer, 2, 2, 8));

            await Task.Delay(200);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        Assert.NotEmpty(sender.Frames);
        Assert.Equal(0x11, sender.Frames[0].FirstByte);
        Assert.Equal(0x22, sender.Frames[1].FirstByte);
    }

    [Fact]
    public async Task BufferedModeMaintainsFifoOrderAfterWarmup()
    {
        var sender = new CollectingSender();
        var options = new NdiVideoPipelineOptions
        {
            EnableBuffering = true,
            BufferDepth = 3,
            TelemetryInterval = TimeSpan.FromDays(1)
        };

        using var pipeline = new NdiVideoPipeline(sender, new FrameRate(30, 1), options, CreateNullLogger());
        pipeline.Start();

        var size = 4 * 2 * 2;
        var buffers = new[]
        {
            Marshal.AllocHGlobal(size),
            Marshal.AllocHGlobal(size),
            Marshal.AllocHGlobal(size),
        };

        try
        {
            FillBuffer(buffers[0], size, 0x31);
            FillBuffer(buffers[1], size, 0x32);
            FillBuffer(buffers[2], size, 0x33);

            pipeline.HandleFrame(new CapturedFrame(buffers[0], 2, 2, 8));
            pipeline.HandleFrame(new CapturedFrame(buffers[1], 2, 2, 8));
            pipeline.HandleFrame(new CapturedFrame(buffers[2], 2, 2, 8));

            await Task.Delay(400);
        }
        finally
        {
            foreach (var handle in buffers)
            {
                Marshal.FreeHGlobal(handle);
            }
        }

        var frames = sender.Frames;
        Assert.True(frames.Count >= 3, "Expected all buffered frames to be sent");
        Assert.Equal(new byte[] { 0x31, 0x32, 0x33 }, frames.Take(3).Select(f => f.FirstByte));
    }

    [Fact]
    public async Task BufferedModeRepeatsLastFrameWhileRewarming()
    {
        var sender = new CollectingSender();
        var options = new NdiVideoPipelineOptions
        {
            EnableBuffering = true,
            BufferDepth = 2,
            TelemetryInterval = TimeSpan.FromDays(1)
        };

        using var pipeline = new NdiVideoPipeline(sender, new FrameRate(30, 1), options, CreateNullLogger());
        pipeline.Start();

        var size = 4 * 2 * 2;
        var first = Marshal.AllocHGlobal(size);
        var second = Marshal.AllocHGlobal(size);
        var third = Marshal.AllocHGlobal(size);

        try
        {
            FillBuffer(first, size, 0x41);
            FillBuffer(second, size, 0x42);
            FillBuffer(third, size, 0x43);

            pipeline.HandleFrame(new CapturedFrame(first, 2, 2, 8));
            pipeline.HandleFrame(new CapturedFrame(second, 2, 2, 8));

            await Task.Delay(250);

            var framesAfterWarmup = sender.Frames.Count;
            Assert.True(framesAfterWarmup >= 2, "Pipeline should have sent the priming frames");

            // Stop feeding the pipeline so it must repeat the last frame.
            await Task.Delay(150);

            var framesBeforeNewData = sender.Frames.Count;
            Assert.True(framesBeforeNewData > framesAfterWarmup, "Pipeline should repeat last frame during underrun");
            Assert.Equal(sender.Frames[^2].FirstByte, sender.Frames[^1].FirstByte);

            // Provide frames one at a time; nothing should be sent until the backlog refills.
            pipeline.HandleFrame(new CapturedFrame(third, 2, 2, 8));

            await Task.Delay(150);
            var afterSingleFrame = sender.Frames.Count;
            Assert.Equal(framesBeforeNewData, afterSingleFrame);

            // Supply a second frame to satisfy the buffer depth.
            pipeline.HandleFrame(new CapturedFrame(first, 2, 2, 8));

            await Task.Delay(250);

            var tail = sender.Frames.TakeLast(2).Select(f => f.FirstByte).ToArray();
            Assert.Contains((byte)0x43, tail);
            Assert.Contains((byte)0x41, tail);
        }
        finally
        {
            Marshal.FreeHGlobal(first);
            Marshal.FreeHGlobal(second);
            Marshal.FreeHGlobal(third);
        }
    }

    private static void FillBuffer(IntPtr buffer, int size, byte value)
    {
        for (var i = 0; i < size; i++)
        {
            Marshal.WriteByte(buffer, i, value);
        }
    }
}

internal sealed class NullSink : ILogEventSink
{
    public void Emit(Serilog.Events.LogEvent logEvent)
    {
    }
}
