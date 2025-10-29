using CefSharp;
using CefSharp.OffScreen;
using Serilog;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Tractus.HtmlToNdi.Video;

/// <summary>
/// Periodically invalidates the Chromium browser to trigger paint events.
/// </summary>
internal sealed class FramePump : IChromiumInvalidator
{
    private static readonly TimeSpan BusyWaitThreshold = TimeSpan.FromMilliseconds(1);

    private readonly ChromiumWebBrowser browser;
    private readonly TimeSpan interval;
    private readonly TimeSpan watchdogInterval;
    private readonly CancellationTokenSource cancellation = new();
    private readonly ILogger logger;
    private readonly object pacedSignalGate = new();

    private TaskCompletionSource<bool> pacedSignal = CreateSignal();
    private Task? pumpTask;
    private Task? watchdogTask;
    private DateTime lastPaint = DateTime.UtcNow;
    private volatile bool isPaused;
    private volatile bool usePacedInvalidation;
    private volatile bool cadenceAlignmentEnabled;
    private volatile double cadenceDriftFrames;
    private int invalidateQueued;

    /// <summary>
    /// Initializes a new instance of the <see cref="FramePump"/> class.
    /// </summary>
    /// <param name="browser">The Chromium browser instance.</param>
    /// <param name="interval">The interval at which to invalidate the browser.</param>
    /// <param name="watchdogInterval">The interval for the watchdog timer.</param>
    /// <param name="logger">The logger instance.</param>
    public FramePump(ChromiumWebBrowser browser, TimeSpan interval, TimeSpan? watchdogInterval, ILogger logger)
    {
        this.browser = browser ?? throw new ArgumentNullException(nameof(browser));
        this.interval = interval;
        this.watchdogInterval = watchdogInterval ?? TimeSpan.FromSeconds(1);
        this.logger = logger;
    }

    /// <inheritdoc />
    public void Start(bool usePacedInvalidation, bool enableCadenceAlignment)
    {
        if (pumpTask is not null)
        {
            return;
        }

        this.usePacedInvalidation = usePacedInvalidation;
        cadenceAlignmentEnabled = enableCadenceAlignment;

        pumpTask = Task.Run(
            usePacedInvalidation
                ? () => RunPacedLoopAsync(cancellation.Token)
                : () => RunPeriodicLoopAsync(cancellation.Token),
            cancellation.Token);

        watchdogTask = Task.Run(() => RunWatchdogAsync(cancellation.Token), cancellation.Token);
    }

    /// <inheritdoc />
    public void NotifyPaint() => lastPaint = DateTime.UtcNow;

    /// <inheritdoc />
    public void RequestInvalidate()
    {
        if (!usePacedInvalidation || cancellation.IsCancellationRequested)
        {
            return;
        }

        if (Volatile.Read(ref isPaused))
        {
            return;
        }

        if (Interlocked.CompareExchange(ref invalidateQueued, 1, 0) != 0)
        {
            return;
        }

        SignalPacedLoop();
    }

    /// <inheritdoc />
    public void PauseInvalidation()
    {
        Volatile.Write(ref isPaused, true);
        if (usePacedInvalidation)
        {
            Interlocked.Exchange(ref invalidateQueued, 0);
        }
    }

    /// <inheritdoc />
    public void ResumeInvalidation()
    {
        var wasPaused = Volatile.Read(ref isPaused);
        Volatile.Write(ref isPaused, false);
        if (wasPaused)
        {
            lastPaint = DateTime.UtcNow;
        }
    }

    /// <inheritdoc />
    public void UpdateCadenceDrift(double deltaFrames)
    {
        if (!cadenceAlignmentEnabled)
        {
            return;
        }

        if (double.IsNaN(deltaFrames) || double.IsInfinity(deltaFrames))
        {
            deltaFrames = 0d;
        }

        Volatile.Write(ref cadenceDriftFrames, deltaFrames);
    }

    private async Task RunPacedLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            Task signalTask;
            lock (pacedSignalGate)
            {
                signalTask = pacedSignal.Task;
            }

            try
            {
                await signalTask.WaitAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            ResetSignal();

            if (token.IsCancellationRequested)
            {
                break;
            }

            var delay = GetPacedDelay();
            if (delay > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(delay, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            if (Volatile.Read(ref isPaused))
            {
                Interlocked.Exchange(ref invalidateQueued, 0);
                continue;
            }

            Interlocked.Exchange(ref invalidateQueued, 0);
            await InvalidateAsync().ConfigureAwait(false);
        }
    }

    private async Task RunPeriodicLoopAsync(CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        var deadline = clock.Elapsed;

        while (!token.IsCancellationRequested)
        {
            var interval = GetPeriodicInterval();
            if (interval <= TimeSpan.Zero)
            {
                interval = TimeSpan.FromMilliseconds(1);
            }

            deadline += interval;
            await WaitUntilAsync(clock, deadline, token).ConfigureAwait(false);

            if (token.IsCancellationRequested)
            {
                break;
            }

            if (Volatile.Read(ref isPaused))
            {
                continue;
            }

            await InvalidateAsync().ConfigureAwait(false);
        }
    }

    private async Task RunWatchdogAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(watchdogInterval, token).ConfigureAwait(false);
                if (token.IsCancellationRequested)
                {
                    break;
                }

                if (Volatile.Read(ref isPaused))
                {
                    continue;
                }

                if (DateTime.UtcNow - lastPaint <= watchdogInterval)
                {
                    continue;
                }

                logger.Debug(
                    "FramePump watchdog: re-invalidate Chromium after {Seconds}s",
                    watchdogInterval.TotalSeconds);

                if (usePacedInvalidation)
                {
                    if (Interlocked.CompareExchange(ref invalidateQueued, 1, 0) != 0)
                    {
                        continue;
                    }
                }

                await InvalidateAsync().ConfigureAwait(false);
                if (usePacedInvalidation)
                {
                    Interlocked.Exchange(ref invalidateQueued, 0);
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
            if (cancellation.IsCancellationRequested)
            {
                return;
            }

            var host = browser.GetBrowserHost();
            if (host is null)
            {
                return;
            }

            await Cef.UIThreadTaskFactory.StartNew(() =>
            {
                if (cancellation.IsCancellationRequested)
                {
                    return;
                }

                if (Volatile.Read(ref isPaused))
                {
                    return;
                }

                host.Invalidate(PaintElementType.View);
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is expected during shutdown or when invalidation is paused.
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "FramePump failed to invalidate Chromium");
        }
    }

    private TimeSpan GetPacedDelay()
    {
        if (!cadenceAlignmentEnabled)
        {
            return TimeSpan.Zero;
        }

        var delta = Volatile.Read(ref cadenceDriftFrames);
        var adjustmentFactor = Math.Clamp(delta * 0.15d, -0.75d, 0.75d);
        if (adjustmentFactor <= 0)
        {
            return TimeSpan.Zero;
        }

        var ticks = (long)Math.Clamp(interval.Ticks * adjustmentFactor, 0, interval.Ticks);
        return ticks <= 0 ? TimeSpan.Zero : TimeSpan.FromTicks(ticks);
    }

    private TimeSpan GetPeriodicInterval()
    {
        if (!cadenceAlignmentEnabled)
        {
            return interval;
        }

        var delta = Volatile.Read(ref cadenceDriftFrames);
        var adjustmentFactor = Math.Clamp(delta * 0.15d, -0.75d, 0.75d);
        var adjustedTicks = interval.Ticks + (long)(interval.Ticks * adjustmentFactor);
        var minimum = TimeSpan.FromMilliseconds(1).Ticks;
        if (adjustedTicks < minimum)
        {
            adjustedTicks = minimum;
        }

        return TimeSpan.FromTicks(adjustedTicks);
    }

    private static TaskCompletionSource<bool> CreateSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void ResetSignal()
    {
        lock (pacedSignalGate)
        {
            if (pacedSignal.Task.IsCompleted)
            {
                pacedSignal = CreateSignal();
            }
        }
    }

    private void SignalPacedLoop()
    {
        lock (pacedSignalGate)
        {
            if (!pacedSignal.Task.IsCompleted)
            {
                pacedSignal.SetResult(true);
            }
        }
    }

    private static async Task WaitUntilAsync(Stopwatch clock, TimeSpan deadline, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var remaining = deadline - clock.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                return;
            }

            if (remaining <= BusyWaitThreshold)
            {
                while (deadline > clock.Elapsed)
                {
                    token.ThrowIfCancellationRequested();
                    Thread.SpinWait(64);
                }

                return;
            }

            var sleep = remaining - BusyWaitThreshold;
            if (sleep < TimeSpan.FromMilliseconds(1))
            {
                sleep = TimeSpan.FromMilliseconds(1);
            }

            try
            {
                await Task.Delay(sleep, token).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                return;
            }
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
        cancellation.Dispose();
    }
}
