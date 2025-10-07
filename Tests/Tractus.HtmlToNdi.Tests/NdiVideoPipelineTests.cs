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

        var size = 4 * 2 * 2;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.WriteByte(buffer, 0, 0x10);
            var frame = new CapturedFrame(buffer, 2, 2, 8);
            pipeline.HandleFrame(frame);
            pipeline.HandleFrame(frame);

            await Task.Delay(300);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            pipeline.Dispose();
        }

        var frames = sender.Frames;
        Assert.True(frames.Count >= 3, "Expected warm-up frames plus a repeat frame");
        Assert.Equal(frames[1].Frame.p_data, frames[2].Frame.p_data);
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

        var size = 4 * 2 * 2;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.WriteByte(buffer, 0, 0x21);
            var frame = new CapturedFrame(buffer, 2, 2, 8);

            pipeline.HandleFrame(frame);
            await Task.Delay(150);
            Assert.Empty(sender.Frames);

            pipeline.HandleFrame(frame);
            pipeline.HandleFrame(frame);
            await Task.Delay(200);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            pipeline.Dispose();
        }

        Assert.True(sender.Frames.Count > 0, "Pipeline should start sending after warm-up");
    }

    [Fact]
    public async Task BufferedModeMaintainsFifoOrderOncePrimed()
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

        var size = 4 * 2 * 2;
        var buffer1 = Marshal.AllocHGlobal(size);
        var buffer2 = Marshal.AllocHGlobal(size);
        var buffer3 = Marshal.AllocHGlobal(size);

        try
        {
            Marshal.WriteByte(buffer1, 0, 0x31);
            Marshal.WriteByte(buffer2, 0, 0x32);
            Marshal.WriteByte(buffer3, 0, 0x33);

            var frame1 = new CapturedFrame(buffer1, 2, 2, 8);
            var frame2 = new CapturedFrame(buffer2, 2, 2, 8);
            var frame3 = new CapturedFrame(buffer3, 2, 2, 8);

            pipeline.HandleFrame(frame1);
            pipeline.HandleFrame(frame2);
            pipeline.HandleFrame(frame3);

            await Task.Delay(300);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer1);
            Marshal.FreeHGlobal(buffer2);
            Marshal.FreeHGlobal(buffer3);
            pipeline.Dispose();
        }

        var frames = sender.Frames;
        Assert.True(frames.Count >= 3, "Expected at least three unique frames");
        Assert.Equal(0x31, frames[0].FirstByte);
        Assert.Equal(0x32, frames[1].FirstByte);
        Assert.Equal(0x33, frames[2].FirstByte);
    }
}

internal sealed class NullSink : ILogEventSink
{
    public void Emit(Serilog.Events.LogEvent logEvent)
    {
    }
}
