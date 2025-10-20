using CefSharp;
using CefSharp.OffScreen;
using Serilog;

namespace Tractus.HtmlToNdi.Video;

/// <summary>
/// Periodically invalidates the Chromium browser to trigger paint events.
/// </summary>
internal sealed class FramePump : IDisposable
{
    private readonly ChromiumWebBrowser browser;
    private readonly TimeSpan interval;
    private readonly TimeSpan watchdogInterval;
    private readonly CancellationTokenSource cancellation = new();
    private readonly ILogger logger;
    private Task? pumpTask;
    private Task? watchdogTask;
    private DateTime lastPaint = DateTime.UtcNow;

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

    /// <summary>
    /// Starts the frame pump.
    /// </summary>
    public void Start()
    {
        if (pumpTask is not null)
        {
            return;
        }

        pumpTask = Task.Run(async () => await RunPumpAsync(cancellation.Token));
        watchdogTask = Task.Run(async () => await RunWatchdogAsync(cancellation.Token));
    }

    /// <summary>
    /// Notifies the frame pump that a paint event has occurred.
    /// </summary>
    public void NotifyPaint() => lastPaint = DateTime.UtcNow;

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
        cancellation.Dispose();
    }
}
