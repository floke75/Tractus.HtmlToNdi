using System;
using System.Threading;
using System.Threading.Tasks;
using CefSharp.OffScreen;

namespace Tractus.HtmlToNdi.Chromium;

internal sealed class FramePump : IDisposable
{
    private readonly ChromiumWebBrowser browser;
    private readonly TimeSpan interval;
    private readonly CancellationTokenSource cancellationTokenSource = new();
    private readonly Task pumpTask;

    public FramePump(ChromiumWebBrowser browser, int frameRate)
    {
        this.browser = browser ?? throw new ArgumentNullException(nameof(browser));
        if (frameRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(frameRate), frameRate, "Frame rate must be positive.");
        }

        this.interval = TimeSpan.FromSeconds(1d / frameRate);
        this.pumpTask = Task.Factory.StartNew(
            this.PumpAsync,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();
    }

    private async Task PumpAsync()
    {
        using var timer = new PeriodicTimer(this.interval);

        try
        {
            while (await timer.WaitForNextTickAsync(this.cancellationTokenSource.Token).ConfigureAwait(false))
            {
                try
                {
                    this.browser.GetBrowserHost()?.Invalidate(CefSharp.PaintElementType.View);
                }
                catch
                {
                    // Swallow exceptions to keep pacing alive; Chromium may already be shutting down.
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown.
        }
    }

    public void Dispose()
    {
        this.cancellationTokenSource.Cancel();
        try
        {
            this.pumpTask.GetAwaiter().GetResult();
        }
        catch
        {
            // Ignore failures from Chromium tearing down concurrently.
        }
        finally
        {
            this.cancellationTokenSource.Dispose();
        }
    }
}
