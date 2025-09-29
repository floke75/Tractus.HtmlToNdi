using System;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace Tractus.HtmlToNdi.Chromium;

internal sealed class FramePacer : IDisposable
{
    private readonly FrameRingBuffer buffer;
    private readonly FramePacingOptions options;
    private readonly IFrameClock clock;
    private readonly ILogger logger;
    private readonly object stateLock = new();
    private CancellationTokenSource? cancellationTokenSource;
    private Task? pacingTask;
    private FrameData? lastFrame;
    private long lastSequence;
    private DateTimeOffset? lastSendTime;
    private bool disposed;

    public FramePacer(FrameRingBuffer buffer, FramePacingOptions options, ILogger? logger = null, IFrameClock? clock = null)
    {
        this.buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.logger = logger ?? Log.Logger;
        this.clock = clock ?? new SystemFrameClock();
    }

    public event Action<FrameDispatchResult>? FrameReady;

    public void Start()
    {
        if (this.pacingTask is not null)
        {
            return;
        }

        this.cancellationTokenSource = new CancellationTokenSource();
        var token = this.cancellationTokenSource.Token;
        this.pacingTask = Task.Run(async () =>
        {
            var nextTick = this.clock.UtcNow;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    await this.clock.DelayUntilAsync(nextTick, token).ConfigureAwait(false);

                    FrameDispatchResult? dispatch;
                    lock (this.stateLock)
                    {
                        dispatch = this.TryGetFrameForDispatchInternal(this.clock.UtcNow);
                    }

                    if (dispatch.HasValue)
                    {
                        try
                        {
                            this.FrameReady?.Invoke(dispatch.Value);
                        }
                        catch (Exception ex)
                        {
                            this.logger.Error(ex, "Error while processing paced frame");
                        }
                    }

                    nextTick += this.options.TargetInterval;
                }
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown.
            }
        }, token);
    }

    public void Stop()
    {
        if (this.cancellationTokenSource is null)
        {
            return;
        }

        this.cancellationTokenSource.Cancel();
        try
        {
            this.pacingTask?.Wait();
        }
        catch (AggregateException ex) when (ex.InnerException is OperationCanceledException)
        {
            // Ignore cancellation exceptions from Wait.
        }
        finally
        {
            this.pacingTask = null;
            this.cancellationTokenSource.Dispose();
            this.cancellationTokenSource = null;
        }
    }

    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.Stop();
        lock (this.stateLock)
        {
            this.lastFrame?.Dispose();
            this.lastFrame = null;
        }

        this.disposed = true;
    }

    internal FrameDispatchResult? TryGetFrameForDispatch(DateTimeOffset now)
    {
        lock (this.stateLock)
        {
            return this.TryGetFrameForDispatchInternal(now);
        }
    }

    private FrameDispatchResult? TryGetFrameForDispatchInternal(DateTimeOffset now)
    {
        var readResult = this.buffer.ReadLatest(this.lastSequence);
        FrameData? frameToSend = null;
        var dropped = 0;
        var isRepeat = false;

        if (readResult.HasFrame)
        {
            frameToSend = readResult.Frame!;
            if (!ReferenceEquals(this.lastFrame, frameToSend))
            {
                this.lastFrame?.Dispose();
            }

            this.lastFrame = frameToSend;
            this.lastSequence = readResult.Sequence;
            dropped = readResult.DroppedCount;
        }
        else if (this.lastFrame is not null)
        {
            frameToSend = this.lastFrame;
            isRepeat = true;
        }

        if (frameToSend is null)
        {
            return null;
        }

        var actualInterval = this.lastSendTime.HasValue ? now - this.lastSendTime.Value : TimeSpan.Zero;
        this.lastSendTime = now;
        return new FrameDispatchResult(frameToSend, isRepeat, dropped, actualInterval, now);
    }
}

internal readonly struct FrameDispatchResult
{
    public FrameDispatchResult(FrameData frame, bool isRepeat, int droppedFrames, TimeSpan actualInterval, DateTimeOffset timestamp)
    {
        this.Frame = frame;
        this.IsRepeat = isRepeat;
        this.DroppedFrames = droppedFrames;
        this.ActualInterval = actualInterval;
        this.Timestamp = timestamp;
    }

    public FrameData Frame { get; }

    public bool IsRepeat { get; }

    public int DroppedFrames { get; }

    public TimeSpan ActualInterval { get; }

    public DateTimeOffset Timestamp { get; }
}
