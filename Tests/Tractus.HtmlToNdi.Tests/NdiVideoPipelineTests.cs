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
            var firstByte = frame.p_data != IntPtr.Zero ? Marshal.ReadByte(frame.p_data) : (byte)0;

            lock (gate)
            {
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
            Marshal.WriteByte(buffer, 0, 0x11);
            var frame = new CapturedFrame(buffer, 2, 2, 8);
            pipeline.HandleFrame(frame);

            await Task.Delay(300);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            pipeline.Dispose();
        }

        var frames = sender.Frames;
        Assert.True(frames.Count >= 2, "Expected at least one repeat frame");
        Assert.Equal(frames[^1].Frame.p_data, frames[^2].Frame.p_data);
    }

    [Fact]
    public async Task BufferedModeWarmsUpBeforeSendingAndKeepsFifoOrder()
    {
        var sender = new CollectingSender();
        var options = new NdiVideoPipelineOptions
        {
            EnableBuffering = true,
            BufferDepth = 3,
            TelemetryInterval = TimeSpan.FromDays(1)
        };

        var pipeline = new NdiVideoPipeline(sender, new FrameRate(5, 1), options, CreateNullLogger());
        pipeline.Start();

        var size = 4;
        var buffers = new IntPtr[3];

        try
        {
            await Task.Delay(250);
            Assert.Empty(sender.Frames);

            for (var i = 0; i < buffers.Length; i++)
            {
                buffers[i] = Marshal.AllocHGlobal(size);
                Marshal.WriteByte(buffers[i], 0, (byte)(0x10 + i));
                pipeline.HandleFrame(new CapturedFrame(buffers[i], 1, 1, size));
            }

            var warmed = SpinWait.SpinUntil(() => sender.Frames.Count >= 3 && pipeline.BufferPrimed, TimeSpan.FromSeconds(2));
            Assert.True(warmed, "Pipeline failed to warm up in time");

            var values = sender.Frames.Select(f => f.FirstByte).ToList();
            Assert.True(values.IndexOf(0x10) >= 0, "Missing first frame");
            Assert.True(values.IndexOf(0x20) > values.IndexOf(0x10), "Frames not sent in FIFO order");
            Assert.True(values.IndexOf(0x30) > values.IndexOf(0x20), "Frames not sent in FIFO order");
            Assert.True(pipeline.LastWarmupDuration > TimeSpan.Zero);
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
    public async Task BufferedModeRewarmsAfterUnderrunAndCountsOnce()
    {
        var sender = new CollectingSender();
        var options = new NdiVideoPipelineOptions
        {
            EnableBuffering = true,
            BufferDepth = 2,
            TelemetryInterval = TimeSpan.FromDays(1)
        };

        var pipeline = new NdiVideoPipeline(sender, new FrameRate(5, 1), options, CreateNullLogger());
        pipeline.Start();

        var size = 4;
        var buffers = new IntPtr[4];

        try
        {
            for (var i = 0; i < 2; i++)
            {
                buffers[i] = Marshal.AllocHGlobal(size);
                Marshal.WriteByte(buffers[i], 0, (byte)(0x40 + i));
                pipeline.HandleFrame(new CapturedFrame(buffers[i], 1, 1, size));
            }

            Assert.True(SpinWait.SpinUntil(() => sender.Frames.Count >= 2 && pipeline.BufferPrimed, TimeSpan.FromSeconds(2)));

            await Task.Delay(700);
            Assert.False(pipeline.BufferPrimed);
            Assert.Equal(1, pipeline.BufferUnderruns);

            for (var i = 2; i < buffers.Length; i++)
            {
                buffers[i] = Marshal.AllocHGlobal(size);
                Marshal.WriteByte(buffers[i], 0, (byte)(0x40 + i));
                pipeline.HandleFrame(new CapturedFrame(buffers[i], 1, 1, size));
            }

            Assert.True(SpinWait.SpinUntil(() => sender.Frames.Count >= 4 && pipeline.BufferPrimed, TimeSpan.FromSeconds(2)));
            Assert.Equal(1, pipeline.BufferUnderruns);

            var markers = sender.Frames.Select(f => f.FirstByte).ToArray();
            Assert.Contains((byte)0x42, markers);
            Assert.Contains((byte)0x43, markers);
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
}

internal sealed class NullSink : ILogEventSink
{
    public void Emit(Serilog.Events.LogEvent logEvent)
    {
    }
}
