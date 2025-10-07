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
    public sealed class SentFrame
    {
        public SentFrame(NDIlib.video_frame_v2_t frame, byte[] data)
        {
            Frame = frame;
            Data = data;
        }

        public NDIlib.video_frame_v2_t Frame { get; }

        public byte[] Data { get; }
    }

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
                var data = new byte[size];
                if (size > 0)
                {
                    Marshal.Copy(frame.p_data, data, 0, size);
                }

                frames.Add(new SentFrame(frame, data));
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
        var buffer1 = Marshal.AllocHGlobal(size);
        var buffer2 = Marshal.AllocHGlobal(size);
        try
        {
            FillBuffer(buffer1, size, 0x11);
            FillBuffer(buffer2, size, 0x22);

            pipeline.HandleFrame(new CapturedFrame(buffer1, 2, 2, 8));
            pipeline.HandleFrame(new CapturedFrame(buffer2, 2, 2, 8));

            await Task.Delay(600);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer1);
            Marshal.FreeHGlobal(buffer2);
            pipeline.Dispose();
        }

        var frames = sender.Frames;
        Assert.True(frames.Count >= 5, "Expected paced pipeline to continue repeating frames while idle");
        var lastActual = frames[^2];
        var repeated = frames[^1];
        Assert.Equal(lastActual.Frame.p_data, repeated.Frame.p_data);
        Assert.Equal(lastActual.Data[0], repeated.Data[0]);
    }

    [Fact]
    public async Task BufferedModeWaitsForWarmupBeforeSending()
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

        try
        {
            await Task.Delay(150);
            Assert.Empty(sender.Frames);

            var size = 4 * 2 * 2;

            var buffer1 = Marshal.AllocHGlobal(size);
            try
            {
                FillBuffer(buffer1, size, 0x11);
                pipeline.HandleFrame(new CapturedFrame(buffer1, 2, 2, 8));
            }
            finally
            {
                Marshal.FreeHGlobal(buffer1);
            }

            await Task.Delay(150);
            Assert.Empty(sender.Frames);

            var buffer2 = Marshal.AllocHGlobal(size);
            try
            {
                FillBuffer(buffer2, size, 0x22);
                pipeline.HandleFrame(new CapturedFrame(buffer2, 2, 2, 8));
            }
            finally
            {
                Marshal.FreeHGlobal(buffer2);
            }

            await Task.Delay(200);

            var frames = sender.Frames;
            Assert.NotEmpty(frames);
            Assert.Equal(0x11, frames[0].Data.First());
        }
        finally
        {
            pipeline.Dispose();
        }
    }

    private static void FillBuffer(IntPtr buffer, int size, byte value)
    {
        var data = Enumerable.Repeat(value, size).Select(b => (byte)b).ToArray();
        Marshal.Copy(data, 0, buffer, size);
    }
}

internal sealed class NullSink : ILogEventSink
{
    public void Emit(Serilog.Events.LogEvent logEvent)
    {
    }
}
