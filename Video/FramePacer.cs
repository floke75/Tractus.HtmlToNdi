using System;
using System.Diagnostics;
using System.Threading;
using Serilog;

namespace Tractus.HtmlToNdi.Video;

public sealed class FramePacer : IDisposable
{
    private readonly FrameRingBuffer buffer;
    private readonly FrameRate targetFrameRate;
    private readonly Action<FrameOutput> frameConsumer;
    private readonly FramePacerOptions options;
    private readonly object stateLock = new();

    private Thread? workerThread;
    private volatile bool running;
    private bool disposed;
    private byte[] currentFrame = Array.Empty<byte>();
    private FrameMetadata currentMetadata;
    private bool hasCurrentFrame;
    private DateTime lastTickUtc = DateTime.MinValue;
    private DateTime lastLogUtc = DateTime.MinValue;
    private readonly Stopwatch stopwatch = new();

    private long sentFrames;
    private long repeatedFrames;
    private long droppedFrames;
    private double minIntervalMs = double.MaxValue;
    private double maxIntervalMs = 0;
    private double accumulatedIntervalMs;

    public FramePacer(FrameRingBuffer buffer, FrameRate targetFrameRate, Action<FrameOutput> frameConsumer, FramePacerOptions? options = null)
    {
        this.buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
        this.targetFrameRate = targetFrameRate;
        this.frameConsumer = frameConsumer ?? throw new ArgumentNullException(nameof(frameConsumer));
        this.options = options ?? new FramePacerOptions();

        if (this.options.StartImmediately)
        {
            this.Start();
        }
    }

    public TimeSpan TargetInterval => this.targetFrameRate.FrameInterval;

    public void Start()
    {
        lock (this.stateLock)
        {
            if (this.running)
            {
                return;
            }

            this.running = true;
            this.workerThread = new Thread(this.Run)
            {
                Name = "FramePacer",
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal,
            };

            this.workerThread.Start();
        }
    }

    public void Stop()
    {
        lock (this.stateLock)
        {
            this.running = false;
        }

        if (this.workerThread is not null && Thread.CurrentThread != this.workerThread)
        {
            this.workerThread.Join();
        }
    }

    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.Stop();
        this.disposed = true;
    }

    internal void RunTick(DateTime utcNow)
    {
        var actualInterval = this.lastTickUtc == DateTime.MinValue
            ? this.TargetInterval
            : utcNow - this.lastTickUtc;
        this.lastTickUtc = utcNow;

        var hasNewFrame = this.TryReadLatestFrame(out var metadata, out var dropped);

        if (hasNewFrame)
        {
            this.currentMetadata = metadata;
            this.hasCurrentFrame = true;
            this.droppedFrames += dropped;
        }
        else if (!this.hasCurrentFrame)
        {
            return;
        }
        else
        {
            this.repeatedFrames++;
        }

        var output = new FrameOutput(this.currentMetadata, new ReadOnlyMemory<byte>(this.currentFrame, 0, this.currentMetadata.BufferLength), !hasNewFrame, hasNewFrame ? dropped : 0, actualInterval);
        this.frameConsumer(output);
        this.sentFrames++;

        this.TrackInterval(actualInterval);
        this.LogMetricsIfNeeded(utcNow);
    }

    private void Run()
    {
        this.stopwatch.Restart();
        var nextDeadline = this.stopwatch.Elapsed;

        while (this.running)
        {
            nextDeadline += this.TargetInterval;
            this.SleepUntil(nextDeadline);
            this.RunTick(DateTime.UtcNow);
        }
    }

    private bool TryReadLatestFrame(out FrameMetadata metadata, out int dropped)
    {
        metadata = default;
        dropped = 0;

        while (true)
        {
            try
            {
                var hasNew = this.buffer.TryCopyLatest(this.currentFrame.AsSpan(), out metadata, out dropped);
                if (!hasNew)
                {
                    return false;
                }

                if (metadata.BufferLength > this.currentFrame.Length)
                {
                    this.currentFrame = new byte[metadata.BufferLength];
                    continue;
                }

                return true;
            }
            catch (ArgumentException)
            {
                var snapshot = this.buffer.GetLatestSnapshot();
                var required = snapshot.HasValue ? snapshot.Metadata.BufferLength : metadata.BufferLength;
                if (required <= 0)
                {
                    return false;
                }

                this.currentFrame = new byte[required];
            }
        }
    }

    private void SleepUntil(TimeSpan target)
    {
        while (true)
        {
            var remaining = target - this.stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                return;
            }

            if (remaining > TimeSpan.FromMilliseconds(2))
            {
                Thread.Sleep(remaining - TimeSpan.FromMilliseconds(1));
            }
            else
            {
                Thread.SpinWait(50);
            }
        }
    }

    private void TrackInterval(TimeSpan interval)
    {
        var intervalMs = interval.TotalMilliseconds;
        this.accumulatedIntervalMs += intervalMs;
        this.minIntervalMs = Math.Min(this.minIntervalMs, intervalMs);
        this.maxIntervalMs = Math.Max(this.maxIntervalMs, intervalMs);
    }

    private void LogMetricsIfNeeded(DateTime utcNow)
    {
        if (this.lastLogUtc != DateTime.MinValue && utcNow - this.lastLogUtc < this.options.MetricsLogInterval)
        {
            return;
        }

        var sent = this.sentFrames;
        if (sent == 0)
        {
            return;
        }

        var avg = this.accumulatedIntervalMs / sent;
        Log.Information("Frame pacer stats: sent={Sent} repeat={Repeated} dropped={Dropped} interval_avg={Avg:F3}ms interval_min={Min:F3}ms interval_max={Max:F3}ms target={Target:F3}ms buffer_depth={BufferDepth}",
            sent,
            this.repeatedFrames,
            this.droppedFrames,
            avg,
            this.minIntervalMs,
            this.maxIntervalMs,
            this.TargetInterval.TotalMilliseconds,
            this.buffer.Capacity);

        this.lastLogUtc = utcNow;
        this.accumulatedIntervalMs = 0;
        this.minIntervalMs = double.MaxValue;
        this.maxIntervalMs = 0;
        this.sentFrames = 0;
        this.repeatedFrames = 0;
        this.droppedFrames = 0;
    }
}
