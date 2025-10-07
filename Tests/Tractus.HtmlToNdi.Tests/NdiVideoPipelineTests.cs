using System;
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
    private sealed class CollectingSender : INdiVideoSender
    {
        private readonly object gate = new();
        private readonly List<NDIlib.video_frame_v2_t> frames = new();

        public IReadOnlyList<NDIlib.video_frame_v2_t> Frames
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
                frames.Add(frame);
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
        Assert.Equal(60, frames[0].frame_rate_N);
        Assert.Equal(1, frames[0].frame_rate_D);
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
            var frame = new CapturedFrame(buffer, 2, 2, 8);
            pipeline.HandleFrame(frame);

            await Task.Delay(200);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            pipeline.Dispose();
        }

        var frames = sender.Frames;
        Assert.True(frames.Count >= 2, "Expected at least one repeat frame");
        Assert.Equal(frames[0].p_data, frames[1].p_data);
    }

    [Fact]
    public async Task BufferedModeWarmsUpBeforeSendingAndUsesFifo()
    {
        var sender = new CollectingSender();
        var options = new NdiVideoPipelineOptions
        {
            EnableBuffering = true,
            BufferDepth = 3,
            TelemetryInterval = TimeSpan.FromDays(1)
        };

        var pipeline = new NdiVideoPipeline(sender, new FrameRate(1, 1), options, CreateNullLogger());
        pipeline.Start();

        var size = 4 * 2 * 2;
        var buffers = new IntPtr[3];

        try
        {
            await Task.Delay(500);
            Assert.Empty(sender.Frames);

            for (var i = 0; i < buffers.Length; i++)
            {
                buffers[i] = Marshal.AllocHGlobal(size);
                Marshal.WriteInt32(buffers[i], i + 1);
                var frame = new CapturedFrame(buffers[i], 2, 2, 8);
                pipeline.HandleFrame(frame);
            }

            await Task.Delay(3500);

            var frames = sender.Frames;
            Assert.True(frames.Count >= 3);
            Assert.Equal(1, Marshal.ReadInt32(frames[0].p_data));
            Assert.Equal(2, Marshal.ReadInt32(frames[1].p_data));
            Assert.Equal(3, Marshal.ReadInt32(frames[2].p_data));
            Assert.True(pipeline.BufferPrimed);
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
    public async Task BufferedModeCountsUnderrunsAndRewarms()
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

        var size = 4 * 2 * 2;
        var buffers = new IntPtr[4];

        try
        {
            for (var i = 0; i < 2; i++)
            {
                buffers[i] = Marshal.AllocHGlobal(size);
                Marshal.WriteInt32(buffers[i], i + 1);
                pipeline.HandleFrame(new CapturedFrame(buffers[i], 2, 2, 8));
            }

            await Task.Delay(1200);

            var firstBurst = sender.Frames;
            Assert.True(firstBurst.Count >= 2);
            Assert.Equal(1, Marshal.ReadInt32(firstBurst[0].p_data));
            Assert.Equal(2, Marshal.ReadInt32(firstBurst[1].p_data));
            Assert.True(pipeline.BufferPrimed);

            await Task.Delay(600);

            Assert.True(pipeline.BufferUnderruns >= 1);
            Assert.False(pipeline.BufferPrimed);

            for (var i = 2; i < 4; i++)
            {
                buffers[i] = Marshal.AllocHGlobal(size);
                Marshal.WriteInt32(buffers[i], i + 1);
                pipeline.HandleFrame(new CapturedFrame(buffers[i], 2, 2, 8));
            }

            await Task.Delay(1200);

            var frames = sender.Frames;
            var values = frames.Select(f => Marshal.ReadInt32(f.p_data)).ToArray();
            var indexOfThree = Array.IndexOf(values, 3);
            var indexOfFour = Array.LastIndexOf(values, 4);

            Assert.True(indexOfThree >= 0, "Expected frame with marker 3 to be sent");
            Assert.True(indexOfFour > indexOfThree, "Expected frame with marker 4 after marker 3");
            Assert.True(pipeline.BufferPrimed);
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
}

internal sealed class NullSink : ILogEventSink
{
    public void Emit(Serilog.Events.LogEvent logEvent)
    {
    }
}
