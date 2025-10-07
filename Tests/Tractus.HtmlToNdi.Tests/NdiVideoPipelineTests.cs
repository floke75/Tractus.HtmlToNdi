using System.Runtime.InteropServices;
using System.Threading;
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
        private readonly List<FrameRecord> frames = new();

        public IReadOnlyList<FrameRecord> Frames
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
            var payload = new byte[frame.yres * frame.line_stride_in_bytes];
            Marshal.Copy(frame.p_data, payload, 0, payload.Length);

            lock (gate)
            {
                frames.Add(new FrameRecord(frame, payload));
            }
        }

        public sealed class FrameRecord
        {
            public FrameRecord(NDIlib.video_frame_v2_t frame, byte[] payload)
            {
                Frame = frame;
                Payload = payload;
            }

            public NDIlib.video_frame_v2_t Frame { get; }
            public byte[] Payload { get; }
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
        Assert.Equal(frames[0].Payload, frames[1].Payload);
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
            for (var i = 0; i < 2; i++)
            {
                WritePattern(buffer, size, (byte)(i + 1));
                pipeline.HandleFrame(new CapturedFrame(buffer, 2, 2, 8));
                await Task.Delay(50);
                Assert.Empty(sender.Frames);
            }

            WritePattern(buffer, size, 0x33);
            pipeline.HandleFrame(new CapturedFrame(buffer, 2, 2, 8));
            var warmed = SpinWait.SpinUntil(() => sender.Frames.Count > 0, TimeSpan.FromMilliseconds(500));
            Assert.True(warmed);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            pipeline.Dispose();
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

        var size = 4 * 2 * 2;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            WritePattern(buffer, size, 0x10);
            pipeline.HandleFrame(new CapturedFrame(buffer, 2, 2, 8));
            WritePattern(buffer, size, 0x20);
            pipeline.HandleFrame(new CapturedFrame(buffer, 2, 2, 8));
            Assert.True(SpinWait.SpinUntil(() => sender.Frames.Count > 0, TimeSpan.FromMilliseconds(500)));

            await Task.Delay(200);
            var initialDistinct = CountDistinctPayloads(sender);

            WritePattern(buffer, size, 0x30);
            pipeline.HandleFrame(new CapturedFrame(buffer, 2, 2, 8));
            await Task.Delay(150);
            Assert.Equal(initialDistinct, CountDistinctPayloads(sender));

            WritePattern(buffer, size, 0x40);
            pipeline.HandleFrame(new CapturedFrame(buffer, 2, 2, 8));
            var rearmed = SpinWait.SpinUntil(() => CountDistinctPayloads(sender) > initialDistinct, TimeSpan.FromMilliseconds(500));
            Assert.True(rearmed);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            pipeline.Dispose();
        }
    }

    private static int CountDistinctPayloads(CollectingSender sender)
    {
        return sender.Frames.Select(frame => frame.Payload[0]).Distinct().Count();
    }

    private static void WritePattern(IntPtr buffer, int size, byte value)
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
