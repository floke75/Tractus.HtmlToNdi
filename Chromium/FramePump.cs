using CefSharp;
using Serilog;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Tractus.HtmlToNdi.Chromium;

internal sealed class FramePump : IDisposable
{
    private readonly Func<IBrowserHost?> hostAccessor;
    private readonly TimeSpan interval;
    private readonly TimeSpan stallThreshold;
    private readonly CancellationTokenSource cancellationTokenSource = new();
    private Task? pumpTask;
    private volatile bool started;
    private DateTime lastInvalidate = DateTime.MinValue;

    public FramePump(Func<IBrowserHost?> hostAccessor, double targetFramesPerSecond, TimeSpan? stallThreshold = null)
    {
        if (targetFramesPerSecond <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetFramesPerSecond));
        }

        this.hostAccessor = hostAccessor ?? throw new ArgumentNullException(nameof(hostAccessor));
        this.interval = TimeSpan.FromSeconds(1d / targetFramesPerSecond);
        this.stallThreshold = stallThreshold ?? TimeSpan.FromSeconds(1);
    }

    public void Start()
    {
        if (this.started)
        {
            return;
        }

        this.started = true;
        this.pumpTask = Task.Run(this.RunAsync);
    }

    private async Task RunAsync()
    {
        using var timer = new PeriodicTimer(this.interval);

        try
        {
            while (await timer.WaitForNextTickAsync(this.cancellationTokenSource.Token).ConfigureAwait(false))
            {
                this.Invalidate();
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown path.
        }
    }

    private void Invalidate()
    {
        try
        {
            var host = this.hostAccessor();
            if (host is null)
            {
                return;
            }

            host.Invalidate(PaintElementType.View);
            this.lastInvalidate = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            Log.Logger.Warning(ex, "Frame pump failed to invalidate Chromium host");
        }
    }

    public void EnsureWatchdog()
    {
        if (!this.started)
        {
            return;
        }

        if (DateTime.UtcNow - this.lastInvalidate >= this.stallThreshold)
        {
            this.Invalidate();
        }
    }

    public async Task StopAsync()
    {
        if (!this.started)
        {
            return;
        }

        this.cancellationTokenSource.Cancel();

        if (this.pumpTask is not null)
        {
            try
            {
                await this.pumpTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected when cancelling.
            }
        }
    }

    public void Dispose()
    {
        this.cancellationTokenSource.Cancel();
        try
        {
            this.pumpTask?.Wait();
        }
        catch (AggregateException ex) when (ex.InnerException is OperationCanceledException)
        {
            // Expected when the pump exits because of cancellation.
        }
        this.cancellationTokenSource.Dispose();
    }
}
