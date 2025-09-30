using Serilog;
using System.Threading;

namespace Tractus.HtmlToNdi.Video;

public sealed class FramePump : IDisposable
{
    private readonly Func<ValueTask> invalidateCallback;
    private readonly TimeSpan interval;
    private readonly TimeSpan watchdogInterval;
    private readonly CancellationTokenSource cts = new();
    private readonly ILogger logger;
    private Task? loopTask;
    private long lastPaintTicks;

    public FramePump(Func<ValueTask> invalidateCallback, TimeSpan interval, ILogger logger, TimeSpan? watchdogInterval = null)
    {
        this.invalidateCallback = invalidateCallback ?? throw new ArgumentNullException(nameof(invalidateCallback));
        this.interval = interval;
        this.logger = logger;
        this.watchdogInterval = watchdogInterval ?? TimeSpan.FromSeconds(1);
    }

    public void Start()
    {
        loopTask = Task.Run(RunAsync);
    }

    public void NotifyPaint(DateTime timestampUtc)
    {
        Interlocked.Exchange(ref lastPaintTicks, timestampUtc.Ticks);
    }

    private async Task RunAsync()
    {
        var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(cts.Token).ConfigureAwait(false))
            {
                await PumpOnceAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // expected on shutdown
        }
        finally
        {
            timer.Dispose();
        }
    }

    private async ValueTask PumpOnceAsync()
    {
        try
        {
            await invalidateCallback().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "Frame pump invalidate failed");
        }

        var lastPaint = new DateTime(Interlocked.Read(ref lastPaintTicks), DateTimeKind.Utc);
        if (lastPaint == DateTime.MinValue)
        {
            return;
        }

        if (DateTime.UtcNow - lastPaint > watchdogInterval)
        {
            try
            {
                await invalidateCallback().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.Warning(ex, "Frame pump watchdog invalidate failed");
            }
        }
    }

    public void Dispose()
    {
        cts.Cancel();
        try
        {
            loopTask?.Wait();
        }
        catch (AggregateException ex) when (ex.InnerException is OperationCanceledException)
        {
            // ignore
        }
        finally
        {
            cts.Dispose();
        }
    }
}
