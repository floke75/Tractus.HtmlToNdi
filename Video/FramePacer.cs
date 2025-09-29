using System;
using System.Diagnostics;
using System.Threading;
using Serilog;

namespace Tractus.HtmlToNdi.Video;

public sealed class FramePacer : IDisposable
{
    private readonly FrameRingBuffer buffer;
    private readonly FrameRate targetFrameRate;
    private readonly Action<FrameDispatch> frameConsumer;
    private readonly FramePacerOptions options;
    private readonly object stateLock = new();
    private readonly Stopwatch stopwatch = new();

    private Thread? workerThread;
    private volatile bool running;
    private bool disposed;
    private VideoFrame? currentFrame;
    private long lastSequence;
    private DateTime lastTickUtc = DateTime.MinValue;
    private DateTime lastLogUtc = DateTime.MinValue;

    private long sentFrames;
    private long repeatedFrames;
    private long droppedFrames;
    private double minIntervalMs = double.MaxValue;
    private double maxIntervalMs;
    private double accumulatedIntervalMs;

    public FramePacer(FrameRingBuffer buffer, FrameRate targetFrameRate, Action<FrameDispatch> frameConsumer, FramePacerOptions? options = null)
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
            this.workerThread = null;
        }
    }

    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.Stop();
        this.currentFrame?.Dispose();
        this.currentFrame = null;
        this.disposed = true;
    }

    internal void RunTick(DateTime utcNow)
    {
        var actualInterval = this.lastTickUtc == DateTime.MinValue ? this.TargetInterval : utcNow - this.lastTickUtc;
        this.lastTickUtc = utcNow;

        var readResult = this.buffer.ReadLatest(this.lastSequence);

        VideoFrame? frameToSend = null;
        var isRepeat = false;
        var dropped = 0;

        if (readResult.HasFrame)
        {
            var latestFrame = readResult.Frame!;
            this.currentFrame?.Dispose();
            this.currentFrame = latestFrame;
            this.lastSequence = readResult.Sequence;
            dropped = readResult.DroppedCount;
            frameToSend = latestFrame;
        }
        else if (this.currentFrame is not null)
        {
            frameToSend = this.currentFrame;
            isRepeat = true;
        }

        if (frameToSend is null)
        {
            return;
        }

        try
        {
            this.frameConsumer(new FrameDispatch(frameToSend, isRepeat, dropped, actualInterval, utcNow));
        }
        catch (Exception ex)
        {
            Log.Logger.Error(ex, "Error while processing paced frame");
        }

        this.sentFrames++;
        if (isRepeat)
        {
            this.repeatedFrames++;
        }

        this.droppedFrames += dropped;
        this.TrackInterval(actualInterval);
        this.LogMetricsIfNeeded(utcNow);
    }

    private void Run()
    {
        this.stopwatch.Restart();
        var nextDeadline = this.stopwatch.Elapsed;

        while (true)
        {
            if (!this.running)
            {
                break;
            }

            nextDeadline += this.TargetInterval;
            this.SleepUntil(nextDeadline);

            if (!this.running)
            {
                break;
            }

            this.RunTick(DateTime.UtcNow);
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
        if (this.options.MetricsLogInterval <= TimeSpan.Zero)
        {
            return;
        }

        if (this.lastLogUtc != DateTime.MinValue && utcNow - this.lastLogUtc < this.options.MetricsLogInterval)
        {
            return;
        }

        if (this.sentFrames == 0)
        {
            return;
        }

        var avg = this.accumulatedIntervalMs / this.sentFrames;
        Log.Information(
            "Frame pacing stats: sent={Sent} repeat={Repeat} dropped={Dropped} interval_avg={Avg:F3}ms interval_min={Min:F3}ms interval_max={Max:F3}ms target={Target:F3}ms buffer_depth={BufferDepth}",
            this.sentFrames,
            this.repeatedFrames,
            this.droppedFrames,
            avg,
            this.minIntervalMs,
            this.maxIntervalMs,
            this.TargetInterval.TotalMilliseconds,
            this.buffer.Capacity);

        this.lastLogUtc = utcNow;
        this.sentFrames = 0;
        this.repeatedFrames = 0;
        this.droppedFrames = 0;
        this.accumulatedIntervalMs = 0;
        this.minIntervalMs = double.MaxValue;
        this.maxIntervalMs = 0;
    }
}