using System.Diagnostics;

namespace Tractus.HtmlToNdi.Chromium;

/// <summary>
/// Pulls frames from a queue at a steady cadence and delivers them to the provided sender callback.
/// </summary>
public sealed class FramePacer : IDisposable
{
    private readonly FrameRingBuffer buffer;
    private readonly Func<VideoFrame, bool> sender;
    private readonly CancellationTokenSource cancellationTokenSource = new();
    private readonly Task worker;
    private readonly TimeSpan interval;
    private readonly object stateGate = new();

    private VideoFrame? currentFrame;
    private bool disposed;

    public FramePacer(FrameRingBuffer buffer, double framesPerSecond, Func<VideoFrame, bool> sender)
    {
        if (framesPerSecond <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(framesPerSecond));
        }

        this.buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
        this.sender = sender ?? throw new ArgumentNullException(nameof(sender));
        this.interval = TimeSpan.FromSeconds(1d / framesPerSecond);
        this.worker = Task.Run(this.RunAsync);
    }

    public long RepeatedFrames { get; private set; }

    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;
        this.cancellationTokenSource.Cancel();

        try
        {
            this.worker.Wait();
        }
        catch (AggregateException ex) when (ex.InnerException is TaskCanceledException)
        {
            // Swallow expected cancellation.
        }
        catch (TaskCanceledException)
        {
        }

        lock (this.stateGate)
        {
            this.currentFrame?.Dispose();
            this.currentFrame = null;
        }

        this.buffer.Dispose();
        this.cancellationTokenSource.Dispose();
    }

    private async Task RunAsync()
    {
        var token = this.cancellationTokenSource.Token;
        var stopwatch = Stopwatch.StartNew();
        long tick = 0;

        while (!token.IsCancellationRequested)
        {
            var nextTarget = this.interval * tick;
            var delay = nextTarget - stopwatch.Elapsed;
            if (delay > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(delay, token).ConfigureAwait(false);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
            }

            VideoFrame? dequeued = null;
            if (this.buffer.TryDequeue(out dequeued) && dequeued is not null)
            {
                lock (this.stateGate)
                {
                    this.currentFrame?.Dispose();
                    this.currentFrame = dequeued;
                }
            }
            else
            {
                if (this.currentFrame is not null)
                {
                    this.RepeatedFrames++;
                }
            }

            VideoFrame? frameToSend;
            lock (this.stateGate)
            {
                frameToSend = this.currentFrame;
            }

            if (frameToSend is null)
            {
                tick++;
                continue;
            }

            if (!this.sender(frameToSend))
            {
                await Task.Delay(this.interval, token).ConfigureAwait(false);
            }

            tick++;
        }
    }
}
