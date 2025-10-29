using CefSharp;
using CefSharp.OffScreen;
using Serilog;
using System.Threading;

namespace Tractus.HtmlToNdi.Video;

internal interface IChromiumInvalidationPump
{
    bool RequestNextInvalidate(TimeSpan? delay = null, TimeSpan? cadenceOffset = null);

    bool CancelPendingInvalidate();

    TimeSpan Interval { get; }
}

/// <summary>
/// Periodically invalidates the Chromium browser to trigger paint events.
/// </summary>
internal sealed class FramePump : IChromiumInvalidationPump, IDisposable
{
    private readonly ChromiumWebBrowser browser;
    private readonly TimeSpan interval;
    private readonly TimeSpan watchdogInterval;
    private readonly CancellationTokenSource cancellation = new();
    private readonly ILogger logger;
    private readonly bool usePacedInvalidation;
    private Task? pumpTask;
    private Task? watchdogTask;
    private DateTime lastPaint = DateTime.UtcNow;
    private readonly object pacedRequestGate = new();
    private CancellationTokenSource? pendingInvalidateCts;
    private int pendingInvalidate;
    private int pacedPauseState;

    /// <summary>
    /// Initializes a new instance of the <see cref="FramePump"/> class.
    /// </summary>
    /// <param name="browser">The Chromium browser instance.</param>
    /// <param name="interval">The interval at which to invalidate the browser.</param>
    /// <param name="watchdogInterval">The interval for the watchdog timer.</param>
    /// <param name="logger">The logger instance.</param>
    /// <param name="usePacedInvalidation">If set to <c>true</c>, invalidations are scheduled on-demand instead of using a periodic timer.</param>
    public FramePump(ChromiumWebBrowser browser, TimeSpan interval, TimeSpan? watchdogInterval, ILogger logger, bool usePacedInvalidation = false)
    {
        this.browser = browser ?? throw new ArgumentNullException(nameof(browser));
        this.interval = interval;
        this.watchdogInterval = watchdogInterval ?? TimeSpan.FromSeconds(1);
        this.logger = logger;
        this.usePacedInvalidation = usePacedInvalidation;
    }

    /// <inheritdoc />
    public TimeSpan Interval => interval;

    /// <summary>
    /// Starts the frame pump.
    /// </summary>
    public void Start()
    {
        if (pumpTask is not null)
        {
            return;
        }

        if (!usePacedInvalidation)
        {
            pumpTask = Task.Run(async () => await RunPumpAsync(cancellation.Token));
        }

        watchdogTask = Task.Run(async () => await RunWatchdogAsync(cancellation.Token));
    }

    /// <summary>
    /// Notifies the frame pump that a paint event has occurred.
    /// </summary>
    public void NotifyPaint() => lastPaint = DateTime.UtcNow;

    public bool RequestNextInvalidate(TimeSpan? delay = null, TimeSpan? cadenceOffset = null)
    {
        if (!usePacedInvalidation)
        {
            return false;
        }

        var effectiveDelay = delay ?? interval;
        if (cadenceOffset.HasValue)
        {
            effectiveDelay += cadenceOffset.Value;
        }

        if (effectiveDelay < TimeSpan.Zero)
        {
            effectiveDelay = TimeSpan.Zero;
        }

        if (effectiveDelay > watchdogInterval)
        {
            effectiveDelay = watchdogInterval;
        }

        CancellationTokenSource? requestCts = null;

        lock (pacedRequestGate)
        {
            if (pendingInvalidate != 0)
            {
                return false;
            }

            pendingInvalidate = 1;
            requestCts = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
            pendingInvalidateCts = requestCts;
            Volatile.Write(ref pacedPauseState, 0);
        }

        _ = Task.Run(async () =>
        {
            try
            {
                if (effectiveDelay > TimeSpan.Zero)
                {
                    await Task.Delay(effectiveDelay, requestCts!.Token).ConfigureAwait(false);
                }

                await InvalidateAsync(requestCts!.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                lock (pacedRequestGate)
                {
                    if (ReferenceEquals(pendingInvalidateCts, requestCts))
                    {
                        pendingInvalidateCts = null;
                        pendingInvalidate = 0;
                    }
                }

                requestCts!.Dispose();
            }
        });

        return true;
    }

    public bool CancelPendingInvalidate()
    {
        if (!usePacedInvalidation)
        {
            return false;
        }

        CancellationTokenSource? requestCts;

        lock (pacedRequestGate)
        {
            requestCts = pendingInvalidateCts;
            pendingInvalidateCts = null;
            pendingInvalidate = 0;
        }

        if (requestCts is null)
        {
            Volatile.Write(ref pacedPauseState, 1);
            return false;
        }

        requestCts.Cancel();
        requestCts.Dispose();
        Volatile.Write(ref pacedPauseState, 1);
        return true;
    }

    private async Task RunPumpAsync(CancellationToken token)
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

    private async Task RunWatchdogAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(watchdogInterval, token);
                if (DateTime.UtcNow - lastPaint > watchdogInterval)
                {
                    if (usePacedInvalidation && Volatile.Read(ref pacedPauseState) == 1)
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

    private async Task InvalidateAsync(CancellationToken token = default)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            var host = browser.GetBrowserHost();
            if (host is null)
            {
                return;
            }

            await Cef.UIThreadTaskFactory.StartNew(() =>
            {
                if (token.IsCancellationRequested)
                {
                    return;
                }

                host.Invalidate(CefSharp.PaintElementType.View);
            });
        }
        catch (OperationCanceledException)
        {
            // cancellation is expected when the pipeline pauses invalidation
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
        CancelPendingInvalidate();
        cancellation.Dispose();
    }
}
