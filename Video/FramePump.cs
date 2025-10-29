using CefSharp;
using CefSharp.OffScreen;
using Serilog;
using System.Threading;
using System.Threading.Channels;

namespace Tractus.HtmlToNdi.Video;

/// <summary>
/// Periodically invalidates the Chromium browser to trigger paint events.
/// </summary>
internal sealed class FramePump : IChromiumInvalidationScheduler
{
    private readonly ChromiumWebBrowser browser;
    private readonly TimeSpan interval;
    private readonly TimeSpan watchdogInterval;
    private readonly CancellationTokenSource cancellation = new();
    private readonly ILogger logger;
    private readonly bool pacedMode;
    private readonly TimeSpan maxAlignmentOffset;
    private readonly Channel<bool>? pacedRequests;
    private readonly AsyncManualResetEvent resumeGate;
    private Task? pumpTask;
    private Task? watchdogTask;
    private DateTime lastPaint = DateTime.UtcNow;
    private double alignmentDeltaFrames;

    /// <summary>
    /// Initializes a new instance of the <see cref="FramePump"/> class.
    /// </summary>
    /// <param name="browser">The Chromium browser instance.</param>
    /// <param name="interval">The interval at which to invalidate the browser.</param>
    /// <param name="watchdogInterval">The interval for the watchdog timer.</param>
    /// <param name="logger">The logger instance.</param>
    /// <param name="pacedMode">Whether Chromium invalidations should be explicitly paced instead of free-running.</param>
    public FramePump(ChromiumWebBrowser browser, TimeSpan interval, TimeSpan? watchdogInterval, ILogger logger, bool pacedMode = false)
    {
        this.browser = browser ?? throw new ArgumentNullException(nameof(browser));
        this.interval = interval;
        this.watchdogInterval = watchdogInterval ?? TimeSpan.FromSeconds(1);
        this.logger = logger;
        this.pacedMode = pacedMode;
        resumeGate = new AsyncManualResetEvent(initialState: true);

        if (pacedMode)
        {
            maxAlignmentOffset = TimeSpan.FromTicks(Math.Max(1, interval.Ticks / 2));
            pacedRequests = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropOldest,
            });
        }
        else
        {
            maxAlignmentOffset = TimeSpan.Zero;
        }
    }

    /// <summary>
    /// Starts the frame pump.
    /// </summary>
    public void Start()
    {
        if (pumpTask is not null)
        {
            return;
        }

        pumpTask = pacedMode
            ? Task.Run(async () => await RunPacedPumpAsync(cancellation.Token))
            : Task.Run(async () => await RunFreeRunningPumpAsync(cancellation.Token));
        watchdogTask = Task.Run(async () => await RunWatchdogAsync(cancellation.Token));
    }

    /// <summary>
    /// Notifies the frame pump that a paint event has occurred.
    /// </summary>
    public void NotifyPaint() => lastPaint = DateTime.UtcNow;

    public void RequestNextInvalidate()
    {
        if (!pacedMode || pacedRequests is null || cancellation.IsCancellationRequested)
        {
            return;
        }

        pacedRequests.Writer.TryWrite(true);
    }

    public void PauseInvalidation()
    {
        if (!pacedMode)
        {
            return;
        }

        resumeGate.Reset();
    }

    public void ResumeInvalidation()
    {
        if (!pacedMode)
        {
            return;
        }

        resumeGate.Set();
    }

    public void UpdateAlignmentDelta(double deltaFrames)
    {
        if (!pacedMode)
        {
            return;
        }

        if (!double.IsFinite(deltaFrames))
        {
            return;
        }

        Volatile.Write(ref alignmentDeltaFrames, Math.Clamp(deltaFrames, -2d, 2d));
    }

    private async Task RunFreeRunningPumpAsync(CancellationToken token)
    {
        var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                await InvalidateAsync();
            }
        }
        catch (OperationCanceledException)
        {
            // expected
        }
        finally
        {
            timer.Dispose();
        }
    }

    private async Task RunPacedPumpAsync(CancellationToken token)
    {
        if (pacedRequests is null)
        {
            return;
        }

        var lastScheduled = DateTime.UtcNow - interval;

        try
        {
            while (await pacedRequests.Reader.WaitToReadAsync(token))
            {
                while (pacedRequests.Reader.TryRead(out _))
                {
                    await resumeGate.WaitAsync(token);
                    token.ThrowIfCancellationRequested();

                    var now = DateTime.UtcNow;
                    var alignment = Math.Clamp(Volatile.Read(ref alignmentDeltaFrames), -1d, 1d);
                    var alignmentTicks = (long)Math.Clamp(alignment * interval.Ticks, -maxAlignmentOffset.Ticks, maxAlignmentOffset.Ticks);
                    var targetTime = lastScheduled + interval + TimeSpan.FromTicks(alignmentTicks);

                    if (targetTime < now)
                    {
                        targetTime = now;
                    }

                    var delay = targetTime - now;
                    if (delay > TimeSpan.Zero)
                    {
                        try
                        {
                            await Task.Delay(delay, token);
                        }
                        catch (TaskCanceledException)
                        {
                            return;
                        }
                    }

                    await InvalidateAsync();
                    lastScheduled = targetTime;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    private async Task RunWatchdogAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(watchdogInterval, token);
                if (DateTime.UtcNow - lastPaint > watchdogInterval)
                {
                    if (pacedMode && !resumeGate.IsSet)
                    {
                        continue;
                    }

                    logger.Debug("FramePump watchdog: re-invalidate Chromium after {Seconds}s", watchdogInterval.TotalSeconds);
                    await InvalidateAsync();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    private async Task InvalidateAsync()
    {
        try
        {
            var host = browser.GetBrowserHost();
            if (host is null)
            {
                return;
            }

            await Cef.UIThreadTaskFactory.StartNew(() =>
            {
                host.Invalidate(CefSharp.PaintElementType.View);
            });
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "FramePump failed to invalidate Chromium");
        }
    }

    /// <summary>
    /// Releases the resources used by the frame pump.
    /// </summary>
    public void Dispose()
    {
        cancellation.Cancel();
        try
        {
            pumpTask?.Wait();
            watchdogTask?.Wait();
        }
        catch (Exception ex) when (ex is AggregateException or OperationCanceledException)
        {
        }

        pumpTask = null;
        watchdogTask = null;
        pacedRequests?.Writer.TryComplete();
        cancellation.Dispose();
    }

    private sealed class AsyncManualResetEvent
    {
        private volatile TaskCompletionSource<bool> completionSource;

        public AsyncManualResetEvent(bool initialState)
        {
            completionSource = CreateSource(initialState);
        }

        public bool IsSet => completionSource.Task.IsCompleted;

        public Task WaitAsync(CancellationToken token)
        {
            var task = completionSource.Task;
            if (task.IsCompleted)
            {
                return task;
            }

            return task.WaitAsync(token);
        }

        public void Set()
        {
            while (true)
            {
                var current = completionSource;
                if (current.Task.IsCompleted)
                {
                    return;
                }

                if (current.TrySetResult(true))
                {
                    return;
                }
            }
        }

        public void Reset()
        {
            while (true)
            {
                var current = completionSource;
                if (!current.Task.IsCompleted)
                {
                    return;
                }

                var newSource = CreateSource(initialState: false);
                if (Interlocked.CompareExchange(ref completionSource, newSource, current) == current)
                {
                    return;
                }
            }
        }

        private static TaskCompletionSource<bool> CreateSource(bool initialState)
        {
            var source = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (initialState)
            {
                source.TrySetResult(true);
            }

            return source;
        }
    }
}
