using CefSharp.OffScreen;
using Serilog;

namespace Tractus.HtmlToNdi.Video;

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

    public FramePump(ChromiumWebBrowser browser, TimeSpan interval, TimeSpan? watchdogInterval, ILogger logger)
    {
        this.browser = browser ?? throw new ArgumentNullException(nameof(browser));
        this.interval = interval;
        this.watchdogInterval = watchdogInterval ?? TimeSpan.FromSeconds(1);
        this.logger = logger;
    }

    public void Start()
    {
        if (pumpTask is not null)
        {
            return;
        }

        pumpTask = Task.Run(async () => await RunPumpAsync(cancellation.Token));
        watchdogTask = Task.Run(async () => await RunWatchdogAsync(cancellation.Token));
    }

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
