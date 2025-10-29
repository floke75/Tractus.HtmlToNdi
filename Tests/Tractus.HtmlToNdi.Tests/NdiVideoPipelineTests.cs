using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Serilog.Core;
using Tractus.HtmlToNdi.Chromium;
using Tractus.HtmlToNdi.Video;
using Xunit;
using Xunit.Abstractions;
using NewTek;
using NewTek.NDI;

namespace Tractus.HtmlToNdi.Tests;

public class NdiVideoPipelineTests
{
    private readonly ITestOutputHelper output;

    public NdiVideoPipelineTests(ITestOutputHelper output)
    {
        this.output = output;
    }

    private sealed record SentFrame(NDIlib.video_frame_v2_t Frame, byte[] Payload, DateTime Timestamp, long MonotonicTimestamp);

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

                frames.Add(new SentFrame(frame, payload, DateTime.UtcNow, Stopwatch.GetTimestamp()));
            }
        }
    }

    private sealed class TestScheduler : IPacedInvalidationScheduler
    {
        private readonly object gate = new();
        private readonly List<TaskCompletionSource<bool>> pending = new();
        private bool paused;
        private bool disposed;
        private int requestCount;
        private int pauseTransitions;
        private int resumeTransitions;
        private double cadenceDelta;

        public int RequestCount => Volatile.Read(ref requestCount);

        public int PauseCount => Volatile.Read(ref pauseTransitions);

        public int ResumeCount => Volatile.Read(ref resumeTransitions);

        public bool IsPaused => Volatile.Read(ref paused);

        public Task RequestInvalidateAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (disposed)
            {
                throw new ObjectDisposedException(nameof(TestScheduler));
            }

            Interlocked.Increment(ref requestCount);

            if (!IsPaused)
            {
                return Task.CompletedTask;
            }

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationTokenRegistration ctr = default;
            if (cancellationToken.CanBeCanceled)
            {
                ctr = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
            }

            lock (gate)
            {
                if (disposed)
                {
                    ctr.Dispose();
                    throw new ObjectDisposedException(nameof(TestScheduler));
                }

                if (!paused)
                {
                    ctr.Dispose();
                    return Task.CompletedTask;
                }

                pending.Add(tcs);
            }

            if (ctr != default)
            {
                _ = tcs.Task.ContinueWith(_ => ctr.Dispose(), TaskScheduler.Default);
            }

            return tcs.Task;
        }

        public void Pause()
        {
            if (disposed)
            {
                return;
            }

            if (!paused)
            {
                paused = true;
                Interlocked.Increment(ref pauseTransitions);
            }
        }

        public void Resume()
        {
            if (disposed)
            {
                return;
            }

            var toRelease = new List<TaskCompletionSource<bool>>();
            lock (gate)
            {
                if (!paused)
                {
                    return;
                }

                paused = false;
                Interlocked.Increment(ref resumeTransitions);
                if (pending.Count > 0)
                {
                    toRelease.AddRange(pending);
                    pending.Clear();
                }
            }

            foreach (var tcs in toRelease)
            {
                tcs.TrySetResult(true);
            }
        }

        public void NotifyPaint()
        {
        }

        public void UpdateCadenceAlignment(double deltaFrames)
        {
            Volatile.Write(ref cadenceDelta, deltaFrames);
        }

        public void Dispose()
        {
            disposed = true;
            List<TaskCompletionSource<bool>> toCancel;
            lock (gate)
            {
                toCancel = new List<TaskCompletionSource<bool>>(pending);
                pending.Clear();
            }

            foreach (var tcs in toCancel)
            {
                tcs.TrySetCanceled();
            }
        }
    }

    private static ILogger CreateNullLogger() => new LoggerConfiguration().WriteTo.Sink(new NullSink()).CreateLogger();

    private static CapturedFrame CreateCapturedFrame(IntPtr buffer, int width, int height, int stride)
    {
        return new CapturedFrame(buffer, width, height, stride, Stopwatch.GetTimestamp(), DateTime.UtcNow);
    }

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
            var frame = CreateCapturedFrame(buffer, 2, 2, 8);
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
                pipeline.HandleFrame(CreateCapturedFrame(buffers[i], 2, 2, 8));
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

            pipeline.HandleFrame(CreateCapturedFrame(buffers[0], 2, 2, 8));
            pipeline.HandleFrame(CreateCapturedFrame(buffers[1], 2, 2, 8));

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
        var buffers = new IntPtr[4];
        try
        {
            for (var i = 0; i < 2; i++)
            {
                buffers[i] = Marshal.AllocHGlobal(frameSize);
                FillBuffer(buffers[i], frameSize, (byte)(0x40 + i));
                pipeline.HandleFrame(CreateCapturedFrame(buffers[i], 2, 2, 8));
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
                pipeline.HandleFrame(CreateCapturedFrame(buffers[i], 2, 2, 8));
            }

            var rearmed = SpinWait.SpinUntil(() => pipeline.BufferPrimed && sender.Frames.Any(f => f.Payload[0] == 0x63), TimeSpan.FromMilliseconds(800));
            Assert.True(rearmed);
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
    public void BufferedPacedInvalidationDropsUnrequestedFrames()
    {
        var sender = new CollectingSender();
        var options = new NdiVideoPipelineOptions
        {
            EnableBuffering = true,
            BufferDepth = 2,
            EnablePacedInvalidation = true,
            TelemetryInterval = TimeSpan.FromDays(1),
        };

        var pipeline = new NdiVideoPipeline(sender, new FrameRate(60, 1), options, CreateNullLogger());
        var scheduler = new TestScheduler();
        pipeline.AttachInvalidationScheduler(scheduler);

        var warmed = SpinWait.SpinUntil(() => scheduler.RequestCount >= 2, TimeSpan.FromMilliseconds(200));
        Assert.True(warmed);

        var frameSize = 4 * 2 * 2;
        var buffers = new IntPtr[3];

        try
        {
            for (var i = 0; i < buffers.Length; i++)
            {
                buffers[i] = Marshal.AllocHGlobal(frameSize);
                FillBuffer(buffers[i], frameSize, (byte)(0x60 + i));
                pipeline.HandleFrame(CreateCapturedFrame(buffers[i], 2, 2, 8));
            }

            Assert.Equal(0, pipeline.PendingInvalidations);
            Assert.Equal(1, pipeline.SpuriousCaptureCount);
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
    public void LatencyExpansionPlaysQueuedFramesBeforeRepeats()
    {
        var sender = new CollectingSender();
        var options = new NdiVideoPipelineOptions
        {
            EnableBuffering = true,
            BufferDepth = 3,
            TelemetryInterval = TimeSpan.FromDays(1),
            AllowLatencyExpansion = true
        };

        var pipeline = new NdiVideoPipeline(sender, new FrameRate(30, 1), options, CreateNullLogger());
        pipeline.Start();

        var frameSize = 4 * 2 * 2;
        var buffers = new IntPtr[5];
        try
        {
            for (var i = 0; i < buffers.Length; i++)
            {
                buffers[i] = Marshal.AllocHGlobal(frameSize);
                FillBuffer(buffers[i], frameSize, (byte)(0x80 + i));
                pipeline.HandleFrame(CreateCapturedFrame(buffers[i], 2, 2, 8));
            }

            var sentAll = SpinWait.SpinUntil(() => sender.Frames.Count >= buffers.Length, TimeSpan.FromMilliseconds(1200));
            Assert.True(sentAll);
            Assert.True(pipeline.BufferUnderruns >= 1);
            Assert.True(pipeline.LatencyExpansionSessions >= 1);

            var uniqueCount = sender.Frames.Take(buffers.Length).Select(f => f.Payload[0]).Distinct().Count();
            Assert.Equal(buffers.Length, uniqueCount);

            var repeated = SpinWait.SpinUntil(
                () => sender.Frames.Count > buffers.Length &&
                      sender.Frames[^1].Payload[0] == sender.Frames[^2].Payload[0],
                TimeSpan.FromMilliseconds(800));
            Assert.True(repeated);
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
    public async Task LatencyExpansionExitsAfterBacklogRecovers()
    {
        var sender = new CollectingSender();
        var options = new NdiVideoPipelineOptions
        {
            EnableBuffering = true,
            BufferDepth = 3,
            TelemetryInterval = TimeSpan.FromDays(1),
            AllowLatencyExpansion = true
        };

        var pipeline = new NdiVideoPipeline(sender, new FrameRate(30, 1), options, CreateNullLogger());
        pipeline.Start();

        var frameSize = 4 * 2 * 2;
        var buffers = new List<IntPtr>();
        try
        {
            for (var i = 0; i < 4; i++)
            {
                var ptr = Marshal.AllocHGlobal(frameSize);
                buffers.Add(ptr);
                FillBuffer(ptr, frameSize, (byte)(0x90 + i));
                pipeline.HandleFrame(CreateCapturedFrame(ptr, 2, 2, 8));
            }

            var primed = SpinWait.SpinUntil(() => pipeline.BufferPrimed, TimeSpan.FromMilliseconds(600));
            Assert.True(primed);

            await Task.Delay(300);

            var expansionStarted = SpinWait.SpinUntil(() => pipeline.LatencyExpansionActive, TimeSpan.FromMilliseconds(800));
            Assert.True(expansionStarted);

            for (var i = 0; i < 4; i++)
            {
                var ptr = Marshal.AllocHGlobal(frameSize);
                buffers.Add(ptr);
                FillBuffer(ptr, frameSize, (byte)(0xA0 + i));
                pipeline.HandleFrame(CreateCapturedFrame(ptr, 2, 2, 8));
            }

            var exited = SpinWait.SpinUntil(() => pipeline.BufferPrimed && !pipeline.LatencyExpansionActive, TimeSpan.FromMilliseconds(1200));
            Assert.True(exited);
            Assert.True(pipeline.LatencyExpansionSessions >= 1);
            Assert.True(pipeline.LatencyExpansionFramesServed > 0);
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
    public async Task BufferedModeDropsFramesWhenAhead()
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
        var buffers = new List<IntPtr>();
        try
        {
            for (var i = 0; i < 6; i++)
            {
                var ptr = Marshal.AllocHGlobal(frameSize);
                buffers.Add(ptr);
                FillBuffer(ptr, frameSize, (byte)(0xB0 + i));
                pipeline.HandleFrame(CreateCapturedFrame(ptr, 2, 2, 8));
            }

            var primed = SpinWait.SpinUntil(() => pipeline.BufferPrimed, TimeSpan.FromMilliseconds(600));
            Assert.True(primed);

            for (var i = 0; i < 12; i++)
            {
                var ptr = Marshal.AllocHGlobal(frameSize);
                buffers.Add(ptr);
                FillBuffer(ptr, frameSize, (byte)(0xC0 + i));
                pipeline.HandleFrame(CreateCapturedFrame(ptr, 2, 2, 8));
            }

            await Task.Delay(600);

            Assert.True(pipeline.BufferPrimed);

            var latencyResyncDropsField = typeof(NdiVideoPipeline)
                .GetField("latencyResyncDrops", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(latencyResyncDropsField);

            var drops = (long)(latencyResyncDropsField!.GetValue(pipeline) ?? 0L);
            Assert.True(drops > 0, "Expected latency resync drops after oversupply");
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
    public async Task PacedInvalidationRequestsStayBounded()
    {
        var sender = new CollectingSender();
        var scheduler = new TestScheduler();
        var options = new NdiVideoPipelineOptions
        {
            EnableBuffering = true,
            BufferDepth = 3,
            TelemetryInterval = TimeSpan.FromDays(1),
            EnablePacedInvalidation = true
        };

        var pipeline = new NdiVideoPipeline(sender, new FrameRate(120, 1), options, CreateNullLogger());
        pipeline.AttachInvalidationScheduler(scheduler);
        pipeline.Start();

        var warmupReady = SpinWait.SpinUntil(() => scheduler.RequestCount >= options.BufferDepth, TimeSpan.FromMilliseconds(500));
        Assert.True(warmupReady);

        var frameSize = 4 * 2 * 2;
        var buffers = new IntPtr[6];
        try
        {
            for (var i = 0; i < buffers.Length; i++)
            {
                buffers[i] = Marshal.AllocHGlobal(frameSize);
                FillBuffer(buffers[i], frameSize, (byte)(0xD0 + i));
                pipeline.HandleFrame(CreateCapturedFrame(buffers[i], 2, 2, 8));
            }

            var sent = SpinWait.SpinUntil(() => sender.Frames.Count >= buffers.Length, TimeSpan.FromMilliseconds(1200));
            Assert.True(sent);

            await Task.Delay(50);
        }
        finally
        {
            var framesSent = sender.Frames.Count;
            pipeline.Dispose();
            var count = scheduler.RequestCount;
            scheduler.Dispose();
            foreach (var ptr in buffers)
            {
                if (ptr != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(ptr);
                }
            }

            Assert.True(count >= options.BufferDepth);
            var slotRequests = count - options.BufferDepth;
            Assert.InRange(slotRequests, framesSent - 1, framesSent + 1);
        }
    }

    [Fact]
    public async Task PacedInvalidationRequestsInDirectMode()
    {
        var sender = new CollectingSender();
        var scheduler = new TestScheduler();
        var options = new NdiVideoPipelineOptions
        {
            EnableBuffering = false,
            TelemetryInterval = TimeSpan.FromDays(1),
            EnablePacedInvalidation = true
        };

        var pipeline = new NdiVideoPipeline(sender, new FrameRate(30, 1), options, CreateNullLogger());
        pipeline.AttachInvalidationScheduler(scheduler);
        pipeline.Start();

        try
        {
            var initialRequest = SpinWait.SpinUntil(() => scheduler.RequestCount >= 1, TimeSpan.FromMilliseconds(500));
            Assert.True(initialRequest);

            var frameSize = 4 * 2 * 2;
            var buffers = new IntPtr[6];

            try
            {
                for (var i = 0; i < buffers.Length; i++)
                {
                    buffers[i] = Marshal.AllocHGlobal(frameSize);
                    FillBuffer(buffers[i], frameSize, (byte)(0x90 + i));
                    pipeline.HandleFrame(CreateCapturedFrame(buffers[i], 2, 2, 8));
                }

                var sent = SpinWait.SpinUntil(() => sender.Frames.Count >= buffers.Length, TimeSpan.FromMilliseconds(500));
                Assert.True(sent);

                await Task.Delay(50);
            }
            finally
            {
                foreach (var ptr in buffers)
                {
                    if (ptr != IntPtr.Zero)
                    {
                        Marshal.FreeHGlobal(ptr);
                    }
                }
            }

            var requests = scheduler.RequestCount;
            Assert.True(requests >= sender.Frames.Count);
            Assert.InRange(requests, sender.Frames.Count, sender.Frames.Count + 2);
        }
        finally
        {
            pipeline.Dispose();
            scheduler.Dispose();
        }
    }

    [Fact]
    public void CaptureBackpressurePausesAndResumes()
    {
        var sender = new CollectingSender();
        var scheduler = new TestScheduler();
        var options = new NdiVideoPipelineOptions
        {
            EnableBuffering = true,
            BufferDepth = 3,
            TelemetryInterval = TimeSpan.FromDays(1),
            EnablePacedInvalidation = true,
            EnableCaptureBackpressure = true
        };

        var pipeline = new NdiVideoPipeline(sender, new FrameRate(90, 1), options, CreateNullLogger());
        pipeline.AttachInvalidationScheduler(scheduler);
        pipeline.Start();

        var primed = SpinWait.SpinUntil(() => pipeline.BufferPrimed, TimeSpan.FromMilliseconds(800));
        Assert.True(primed);

        var frameSize = 4 * 2 * 2;
        var buffers = new List<IntPtr>();
        try
        {
            for (var i = 0; i < 12; i++)
            {
                var ptr = Marshal.AllocHGlobal(frameSize);
                buffers.Add(ptr);
                FillBuffer(ptr, frameSize, (byte)(0xE0 + i));
                pipeline.HandleFrame(CreateCapturedFrame(ptr, 2, 2, 8));
            }

            var paused = SpinWait.SpinUntil(() => scheduler.PauseCount >= 1 && pipeline.CaptureGateActive, TimeSpan.FromMilliseconds(1200));
            Assert.True(paused);

            var drained = SpinWait.SpinUntil(() => scheduler.ResumeCount >= 1 && !pipeline.CaptureGateActive, TimeSpan.FromMilliseconds(2000));
            Assert.True(drained);
        }
        finally
        {
            var requestCount = scheduler.RequestCount;
            pipeline.Dispose();
            scheduler.Dispose();
            foreach (var ptr in buffers)
            {
                if (ptr != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(ptr);
                }
            }
            Assert.True(requestCount >= options.BufferDepth);
        }
    }

    [Fact]
    public void CalculateNextDeadlineDelaysOrHastensBasedOnBacklog()
    {
        var sender = new CollectingSender();
        var options = new NdiVideoPipelineOptions
        {
            EnableBuffering = true,
            BufferDepth = 4,
            TelemetryInterval = TimeSpan.FromDays(1)
        };

        var pipeline = new NdiVideoPipeline(sender, new FrameRate(60, 1), options, CreateNullLogger());
        var frameInterval = pipeline.FrameRate.FrameDuration;

        var ringBufferField = typeof(NdiVideoPipeline)
            .GetField("ringBuffer", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(ringBufferField);

        var bufferPrimedField = typeof(NdiVideoPipeline)
            .GetField("bufferPrimed", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(bufferPrimedField);

        var latencyErrorField = typeof(NdiVideoPipeline)
            .GetField("latencyError", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(latencyErrorField);

        var calculateNextDeadline = typeof(NdiVideoPipeline)
            .GetMethod("CalculateNextDeadline", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(calculateNextDeadline);

        var ringBuffer = (FrameRingBuffer<NdiVideoFrame>?)ringBufferField!.GetValue(pipeline);
        Assert.NotNull(ringBuffer);

        bufferPrimedField!.SetValue(pipeline, true);

        var frameSize = 4 * 2 * 2;
        var buffer = Marshal.AllocHGlobal(frameSize);

        try
        {
            FillBuffer(buffer, frameSize, 0x55);

            void PopulateBacklog(int count)
            {
                ringBuffer!.Clear();
                for (var i = 0; i < count; i++)
                {
                    pipeline.HandleFrame(CreateCapturedFrame(buffer, 2, 2, 8));
                }
            }

            TimeSpan InvokeDeadline()
            {
                return (TimeSpan)calculateNextDeadline!.Invoke(pipeline, new object[] { TimeSpan.Zero, 1L })!;
            }

            PopulateBacklog(options.BufferDepth - 1);
            latencyErrorField!.SetValue(pipeline, 0d);
            var underDeadline = InvokeDeadline();
            Assert.True(underDeadline - frameInterval > TimeSpan.Zero, "Backlog deficit should delay the deadline.");

            PopulateBacklog(options.BufferDepth + 1);
            latencyErrorField.SetValue(pipeline, 0d);
            var overDeadline = InvokeDeadline();
            Assert.True(overDeadline - frameInterval < TimeSpan.Zero, "Oversupply should hasten the deadline.");

            PopulateBacklog(options.BufferDepth);
            latencyErrorField.SetValue(pipeline, (double)options.BufferDepth);
            var integralAccelerates = InvokeDeadline();
            Assert.True(integralAccelerates - frameInterval < TimeSpan.Zero, "Positive latencyError should hasten pacing.");

            latencyErrorField.SetValue(pipeline, -(double)options.BufferDepth);
            var integralDelays = InvokeDeadline();
            Assert.True(integralDelays - frameInterval > TimeSpan.Zero, "Negative latencyError should delay pacing.");
        }
        finally
        {
            pipeline.Dispose();
            if (buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    [Fact]
    public void TrySendBufferedFrameMaintainsIntegratorSign()
    {
        var options = new NdiVideoPipelineOptions
        {
            EnableBuffering = true,
            BufferDepth = 3,
            TelemetryInterval = TimeSpan.FromDays(1)
        };

        static (bool Sent, double LatencyError) InvokeSend(int backlog, NdiVideoPipelineOptions options)
        {
            var sender = new CollectingSender();
            var pipeline = new NdiVideoPipeline(sender, new FrameRate(60, 1), options, CreateNullLogger());

            var trySend = typeof(NdiVideoPipeline)
                .GetMethod("TrySendBufferedFrame", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(trySend);

            var bufferPrimedField = typeof(NdiVideoPipeline)
                .GetField("bufferPrimed", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(bufferPrimedField);

            var isWarmingUpField = typeof(NdiVideoPipeline)
                .GetField("isWarmingUp", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(isWarmingUpField);

            var latencyErrorField = typeof(NdiVideoPipeline)
                .GetField("latencyError", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(latencyErrorField);

            var frameSize = 4 * 2 * 2;
            var buffer = Marshal.AllocHGlobal(frameSize);

            try
            {
                FillBuffer(buffer, frameSize, 0x5A);

                for (var i = 0; i < backlog; i++)
                {
                    pipeline.HandleFrame(CreateCapturedFrame(buffer, 2, 2, 8));
                }

                bufferPrimedField!.SetValue(pipeline, true);
                isWarmingUpField!.SetValue(pipeline, false);
                latencyErrorField!.SetValue(pipeline, 0d);

                var sent = (bool)trySend!.Invoke(pipeline, Array.Empty<object>())!;
                var latency = (double)(latencyErrorField.GetValue(pipeline) ?? 0d);

                return (sent, latency);
            }
            finally
            {
                pipeline.Dispose();

                if (buffer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
        }

        var (sentOversupplied, latencyOversupplied) = InvokeSend(options.BufferDepth + 1, options);
        Assert.True(sentOversupplied);
        Assert.True(latencyOversupplied > 0, "Oversupply should leave a positive latencyError.");

        var (sentUndersupplied, latencyUndersupplied) = InvokeSend(options.BufferDepth - 1, options);
        Assert.True(sentUndersupplied);
        Assert.True(latencyUndersupplied < 0, "Undersupply should leave a negative latencyError.");
    }

    [Fact]
    public async Task LatencyErrorConvergesNearZeroWithBuffering()
    {
        var sender = new CollectingSender();
        var options = new NdiVideoPipelineOptions
        {
            EnableBuffering = true,
            BufferDepth = 3,
            TelemetryInterval = TimeSpan.FromMilliseconds(200)
        };

        var pipeline = new NdiVideoPipeline(sender, new FrameRate(60, 1), options, CreateNullLogger());
        pipeline.Start();

        var frameInterval = pipeline.FrameRate.FrameDuration;
        var frameSize = 4 * 2 * 2;
        var buffer = Marshal.AllocHGlobal(frameSize);
        var cts = new CancellationTokenSource();
        Task producerTask = Task.CompletedTask;

        try
        {
            FillBuffer(buffer, frameSize, 0x77);

            producerTask = Task.Run(async () =>
            {
                try
                {
                    while (!cts.IsCancellationRequested)
                    {
                        pipeline.HandleFrame(CreateCapturedFrame(buffer, 2, 2, 8));
                        await Task.Delay(frameInterval, cts.Token);
                    }
                }
                catch (OperationCanceledException)
                {
                }
            });

            var primed = SpinWait.SpinUntil(
                () => pipeline.BufferPrimed && sender.Frames.Count >= options.BufferDepth,
                TimeSpan.FromSeconds(2));
            Assert.True(primed);

            await Task.Delay(TimeSpan.FromMilliseconds(frameInterval.TotalMilliseconds * 120));

            var frames = sender.Frames;
            Assert.True(frames.Count > options.BufferDepth + 5, "Not enough frames captured for cadence analysis.");

            var analysisFrames = frames.Skip(Math.Max(0, frames.Count - 60)).ToArray();
            Assert.True(analysisFrames.Length >= 2, "Need at least two frames for cadence analysis.");

            var intervals = analysisFrames
                .Zip(analysisFrames.Skip(1), (first, second) =>
                {
                    var delta = second.MonotonicTimestamp - first.MonotonicTimestamp;
                    return delta / (double)Stopwatch.Frequency;
                })
                .ToArray();

            Assert.NotEmpty(intervals);

            var frameIntervalSeconds = frameInterval.TotalSeconds;
            var averageInterval = intervals.Average();
            var maxDeviation = intervals.Max(delta => Math.Abs(delta - frameIntervalSeconds));

            var latencyErrorField = typeof(NdiVideoPipeline)
                .GetField("latencyError", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(latencyErrorField);
            var latencyError = (double)(latencyErrorField!.GetValue(pipeline) ?? 0d);

            var offsetField = typeof(NdiVideoPipeline)
                .GetField("lastPacingOffsetTicks", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(offsetField);
            var offsetTicks = (long)(offsetField!.GetValue(pipeline) ?? 0L);
            var offsetMs = offsetTicks / (double)TimeSpan.TicksPerMillisecond;

            output.WriteLine(
                $"latencyError={latencyError:F3}, offsetMs={offsetMs:F3}, avgIntervalMs={averageInterval * 1000:F3}, maxDeviationMs={maxDeviation * 1000:F3}, samples={intervals.Length}");

            Assert.InRange(latencyError, -0.6, 0.6);
            Assert.InRange(offsetMs, -0.6, 0.6);
            Assert.InRange(averageInterval, frameIntervalSeconds * 0.95, frameIntervalSeconds * 1.05);
            Assert.InRange(maxDeviation, 0, 0.004);
        }
        finally
        {
            cts.Cancel();
            try
            {
                await producerTask;
            }
            catch (OperationCanceledException)
            {
            }

            pipeline.Dispose();
            if (buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    [Fact]
    public void CaptureBackpressureRequiresPacedInvalidation()
    {
        var sender = new CollectingSender();
        var scheduler = new TestScheduler();
        var options = new NdiVideoPipelineOptions
        {
            EnableBuffering = true,
            BufferDepth = 3,
            TelemetryInterval = TimeSpan.FromDays(1),
            EnableCaptureBackpressure = true,
            EnablePacedInvalidation = false
        };

        var pipeline = new NdiVideoPipeline(sender, new FrameRate(60, 1), options, CreateNullLogger());
        pipeline.AttachInvalidationScheduler(scheduler);
        pipeline.Start();

        var frameSize = 4 * 2 * 2;
        var buffers = new List<IntPtr>();

        try
        {
            for (var i = 0; i < 12; i++)
            {
                var ptr = Marshal.AllocHGlobal(frameSize);
                buffers.Add(ptr);
                FillBuffer(ptr, frameSize, (byte)(0xA0 + i));
                pipeline.HandleFrame(CreateCapturedFrame(ptr, 2, 2, 8));
            }

            var primed = SpinWait.SpinUntil(() => pipeline.BufferPrimed, TimeSpan.FromMilliseconds(800));
            Assert.True(primed);

            var sent = SpinWait.SpinUntil(() => sender.Frames.Count >= buffers.Count, TimeSpan.FromMilliseconds(1200));
            Assert.True(sent);

            Assert.Equal(0, scheduler.PauseCount);
            Assert.Equal(0, scheduler.ResumeCount);
            Assert.False(pipeline.CaptureGateActive);
        }
        finally
        {
            pipeline.Dispose();
            scheduler.Dispose();
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
