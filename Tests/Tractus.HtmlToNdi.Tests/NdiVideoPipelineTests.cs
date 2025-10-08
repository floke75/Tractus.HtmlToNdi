using System.Linq;
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
        var buffers = new IntPtr[4];
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

            var warmed = SpinWait.SpinUntil(() => sender.Frames.Count >= buffers.Length, TimeSpan.FromMilliseconds(1000));
            Assert.True(warmed);
            Assert.True(pipeline.BufferPrimed);

            var frames = sender.Frames.Take(buffers.Length).ToArray();
            Assert.Equal(0x10, frames[0].Payload[0]);
            Assert.Equal(0x11, frames[1].Payload[0]);
            Assert.Equal(0x12, frames[2].Payload[0]);
            Assert.Equal(0x13, frames[3].Payload[0]);
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
            BufferDepth = 3,
            TelemetryInterval = TimeSpan.FromDays(1)
        };

        var pipeline = new NdiVideoPipeline(sender, new FrameRate(30, 1), options, CreateNullLogger());
        pipeline.Start();

        var frameSize = 4 * 2 * 2;
        var buffers = new IntPtr[4];
        try
        {
            for (var i = 0; i < buffers.Length; i++)
            {
                buffers[i] = Marshal.AllocHGlobal(frameSize);
                FillBuffer(buffers[i], frameSize, (byte)(0x20 + i));
                pipeline.HandleFrame(new CapturedFrame(buffers[i], 2, 2, 8));
            }

            var primed = SpinWait.SpinUntil(() => sender.Frames.Count >= buffers.Length, TimeSpan.FromMilliseconds(800));
            Assert.True(primed);

            await Task.Delay(400);

            var frames = sender.Frames;
            Assert.True(frames.Count >= buffers.Length + 2, "Expected repeated frames while idle");
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
            BufferDepth = 3,
            TelemetryInterval = TimeSpan.FromDays(1)
        };

        var pipeline = new NdiVideoPipeline(sender, new FrameRate(30, 1), options, CreateNullLogger());
        pipeline.Start();

        var frameSize = 4 * 2 * 2;
        var initialPayloads = new byte[] { 0x40, 0x41, 0x42, 0x43 };
        var recoveryPayloads = new byte[] { 0xA0, 0xA1, 0xA2, 0xA3 };
        var buffers = new IntPtr[initialPayloads.Length + recoveryPayloads.Length];
        try
        {
            for (var i = 0; i < initialPayloads.Length; i++)
            {
                buffers[i] = Marshal.AllocHGlobal(frameSize);
                FillBuffer(buffers[i], frameSize, initialPayloads[i]);
                pipeline.HandleFrame(new CapturedFrame(buffers[i], 2, 2, 8));
            }

            var primed = SpinWait.SpinUntil(
                () => pipeline.BufferPrimed && sender.Frames.Count >= initialPayloads.Length,
                TimeSpan.FromMilliseconds(800));
            Assert.True(primed);

            var initialFrameCount = sender.Frames.Count;
            var repeatedPayload = sender.Frames[^1].Payload[0];

            var underrunDetected = SpinWait.SpinUntil(
                () => pipeline.BufferUnderruns >= 1 && !pipeline.BufferPrimed,
                TimeSpan.FromMilliseconds(800));
            Assert.True(underrunDetected);

            for (var i = 0; i < recoveryPayloads.Length; i++)
            {
                var index = initialPayloads.Length + i;
                buffers[index] = Marshal.AllocHGlobal(frameSize);
                FillBuffer(buffers[index], frameSize, recoveryPayloads[i]);
                pipeline.HandleFrame(new CapturedFrame(buffers[index], 2, 2, 8));
            }

            var rearmed = SpinWait.SpinUntil(
                () => pipeline.BufferPrimed && sender.Frames.Any(f => f.Payload[0] == recoveryPayloads[^1]),
                TimeSpan.FromMilliseconds(1200));
            Assert.True(rearmed);
            Assert.True(pipeline.LastWarmupDuration > TimeSpan.Zero);
            Assert.Equal(1, pipeline.BufferUnderruns);

            var framesAfterUnderrun = sender.Frames.Skip(initialFrameCount).ToArray();
            var freshFrames = framesAfterUnderrun.Where(f => f.Payload[0] != repeatedPayload).ToArray();
            Assert.True(freshFrames.Length >= recoveryPayloads.Length);
            var deliveredPayloads = freshFrames.Take(recoveryPayloads.Length).Select(f => f.Payload[0]).ToArray();
            Assert.Equal(recoveryPayloads, deliveredPayloads);

            var stalePayloads = initialPayloads.Except(new[] { repeatedPayload }).ToHashSet();
            Assert.DoesNotContain(freshFrames.Select(f => f.Payload[0]), p => stalePayloads.Contains(p));
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
    public async Task BufferedModeHoldsFramesUntilDepthRecovered()
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
        var primingPayloads = new byte[] { 0x70, 0x71, 0x72, 0x73 };
        var partialPayloads = new byte[] { 0xC0, 0xC1, 0xC2 };
        var triggerPayload = (byte)0xC3;
        var totalBuffers = primingPayloads.Length + partialPayloads.Length + 1;
        var buffers = new IntPtr[totalBuffers];

        try
        {
            for (var i = 0; i < primingPayloads.Length; i++)
            {
                buffers[i] = Marshal.AllocHGlobal(frameSize);
                FillBuffer(buffers[i], frameSize, primingPayloads[i]);
                pipeline.HandleFrame(new CapturedFrame(buffers[i], 2, 2, 8));
            }

            var primed = SpinWait.SpinUntil(
                () => pipeline.BufferPrimed && sender.Frames.Count >= primingPayloads.Length,
                TimeSpan.FromMilliseconds(800));
            Assert.True(primed);

            var initialFrameCount = sender.Frames.Count;
            var repeatedPayload = sender.Frames[^1].Payload[0];

            var underrunDetected = SpinWait.SpinUntil(
                () => pipeline.BufferUnderruns >= 1 && !pipeline.BufferPrimed,
                TimeSpan.FromMilliseconds(800));
            Assert.True(underrunDetected);

            for (var i = 0; i < partialPayloads.Length; i++)
            {
                var index = primingPayloads.Length + i;
                buffers[index] = Marshal.AllocHGlobal(frameSize);
                FillBuffer(buffers[index], frameSize, partialPayloads[i]);
                pipeline.HandleFrame(new CapturedFrame(buffers[index], 2, 2, 8));
            }

            await Task.Delay(400);
            Assert.False(pipeline.BufferPrimed);
            Assert.False(sender.Frames.Skip(initialFrameCount).Any(f => partialPayloads.Contains(f.Payload[0])));

            var triggerIndex = primingPayloads.Length + partialPayloads.Length;
            buffers[triggerIndex] = Marshal.AllocHGlobal(frameSize);
            FillBuffer(buffers[triggerIndex], frameSize, triggerPayload);
            pipeline.HandleFrame(new CapturedFrame(buffers[triggerIndex], 2, 2, 8));

            var rearmed = SpinWait.SpinUntil(
                () => pipeline.BufferPrimed && sender.Frames.Any(f => f.Payload[0] == triggerPayload),
                TimeSpan.FromMilliseconds(1200));
            Assert.True(rearmed);

            var framesAfterUnderrun = sender.Frames.Skip(initialFrameCount).ToArray();
            var freshFrames = framesAfterUnderrun.Where(f => f.Payload[0] != repeatedPayload).ToArray();
            Assert.True(freshFrames.Length >= partialPayloads.Length + 1);

            var expected = partialPayloads.Append(triggerPayload).ToArray();
            var delivered = freshFrames.Take(expected.Length).Select(f => f.Payload[0]).ToArray();
            Assert.Equal(expected, delivered);
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
