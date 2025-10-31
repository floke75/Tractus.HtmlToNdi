using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using CefSharp;
using CefSharp.OffScreen;
using Serilog;

namespace Tractus.HtmlToNdi.Chromium;

public interface IPacedInvalidationScheduler : IDisposable
{
    Task RequestInvalidateAsync(CancellationToken cancellationToken = default);

    void Pause();

    void Resume();

    void NotifyPaint();

    void UpdateCadenceAlignment(double deltaFrames);

    bool IsPaused { get; }
}

internal enum FramePumpMode
{
    Periodic,
    OnDemand,
}

internal sealed class FramePump : IPacedInvalidationScheduler
{
    private const double MaxCadenceAdjustmentFrames = 0.5d;
    private const double CadenceAdaptationGain = 0.25d;

    private readonly ChromiumWebBrowser browser;
    private readonly TimeSpan baseInterval;
    private readonly TimeSpan watchdogInterval;
    private readonly ILogger logger;
    private readonly FramePumpMode mode;
    private readonly bool cadenceAdaptationEnabled;
    private readonly Func<CancellationToken, Task> invalidateBrowserAsync;
    private readonly Channel<InvalidationRequest> requestChannel;
    private readonly CancellationTokenSource cancellation = new();
    private readonly object stateGate = new();
    private readonly ConcurrentQueue<InvalidationRequest> pausedQueue = new();

    private Task? processingTask;
    private Task? periodicTask;
    private Task? watchdogTask;
    private volatile bool paused;
    private volatile bool started;
    private double cadenceAlignmentDeltaFrames;
    private long lastPaintTicks = DateTime.UtcNow.Ticks;
    private bool disposed;

    public FramePump(
        ChromiumWebBrowser browser,
        TimeSpan interval,
        TimeSpan? watchdogInterval,
        ILogger logger,
        FramePumpMode mode,
        bool cadenceAdaptationEnabled,
        Func<ChromiumWebBrowser, ILogger, CancellationToken, Task>? invalidateBrowser = null)
    {
        this.browser = browser ?? throw new ArgumentNullException(nameof(browser));
        baseInterval = interval;
        this.watchdogInterval = watchdogInterval ?? TimeSpan.FromSeconds(1);
        this.logger = logger;
        this.mode = mode;
        this.cadenceAdaptationEnabled = cadenceAdaptationEnabled;
        var invalidate = invalidateBrowser ?? DefaultInvalidateBrowserAsync;
        invalidateBrowserAsync = token => invalidate(this.browser, this.logger, token);

        requestChannel = Channel.CreateUnbounded<InvalidationRequest>(new UnboundedChannelOptions
        {
            SingleReader = true,
            AllowSynchronousContinuations = false,
        });
    }

    public bool IsPaused => paused;

    public void Start()
    {
        ThrowIfDisposed();
        if (started)
        {
            return;
        }

        lock (stateGate)
        {
            if (started)
            {
                return;
            }

            processingTask = StartDedicatedTask(ProcessRequestsAsync);
            if (mode == FramePumpMode.Periodic)
            {
                periodicTask = StartDedicatedTask(RunPeriodicLoopAsync);
            }

            watchdogTask = StartDedicatedTask(RunWatchdogAsync);
            started = true;
        }
    }

    public async Task RequestInvalidateAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token, cancellationToken);
        var request = new InvalidationRequest(linkedCancellation.Token);

        try
        {
            await requestChannel.Writer.WriteAsync(request, linkedCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            request.Dispose();
            linkedCancellation.Dispose();
            throw;
        }
        catch (ChannelClosedException ex)
        {
            request.Dispose();
            linkedCancellation.Dispose();
            if (cancellation.IsCancellationRequested)
            {
                throw new OperationCanceledException(ex.Message, ex);
            }

            throw new ObjectDisposedException(nameof(FramePump), ex);
        }

        try
        {
            await request.Completion.WaitAsync(linkedCancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            request.Dispose();
            linkedCancellation.Dispose();
        }
    }

    public void Pause()
    {
        ThrowIfDisposed();
        paused = true;
    }

    public void Resume()
    {
        ThrowIfDisposed();

        if (!paused)
        {
            return;
        }

        paused = false;

        Interlocked.Exchange(ref lastPaintTicks, DateTime.UtcNow.Ticks);

        while (pausedQueue.TryDequeue(out var pending))
        {
            if (pending.IsCancellationRequested)
            {
                pending.Dispose();
                continue;
            }

            if (!requestChannel.Writer.TryWrite(pending))
            {
                pending.Dispose();
            }
        }
    }

    public void NotifyPaint()
    {
        Interlocked.Exchange(ref lastPaintTicks, DateTime.UtcNow.Ticks);
    }

    public void UpdateCadenceAlignment(double deltaFrames)
    {
        if (double.IsNaN(deltaFrames))
        {
            return;
        }

        Interlocked.Exchange(ref cadenceAlignmentDeltaFrames, deltaFrames);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        cancellation.Cancel();
        requestChannel.Writer.TryComplete();

        try
        {
            processingTask?.Wait();
        }
        catch (Exception ex) when (ex is OperationCanceledException or TaskCanceledException or AggregateException)
        {
        }

        try
        {
            periodicTask?.Wait();
        }
        catch (Exception ex) when (ex is OperationCanceledException or TaskCanceledException or AggregateException)
        {
        }

        try
        {
            watchdogTask?.Wait();
        }
        catch (Exception ex) when (ex is OperationCanceledException or TaskCanceledException or AggregateException)
        {
        }

        cancellation.Dispose();

        while (pausedQueue.TryDequeue(out var pending))
        {
            pending.Dispose();
        }
    }

    private async Task ProcessRequestsAsync(CancellationToken token)
    {
        try
        {
            while (await requestChannel.Reader.WaitToReadAsync(token).ConfigureAwait(false))
            {
                while (requestChannel.Reader.TryRead(out var request))
                {
                    if (request.IsCancellationRequested)
                    {
                        request.Dispose();
                        continue;
                    }

                    if (paused)
                    {
                        pausedQueue.Enqueue(request);
                        continue;
                    }

                    await ExecuteRequestAsync(request, token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            while (requestChannel.Reader.TryRead(out var remaining))
            {
                remaining.Dispose();
            }
        }
    }

    private async Task ExecuteRequestAsync(InvalidationRequest request, CancellationToken token)
    {
        try
        {
            if (mode == FramePumpMode.OnDemand)
            {
                await ApplyOnDemandCadenceDelayAsync(token).ConfigureAwait(false);
            }

            Task invalidateTask;
            try
            {
                invalidateTask = invalidateBrowserAsync(token);
            }
            catch (OperationCanceledException ex)
            {
                request.Fail(ex);
                throw;
            }
            catch (Exception ex)
            {
                request.Fail(ex);
                logger.Warning(ex, "FramePump failed to invalidate Chromium");
                return;
            }

            if (invalidateTask.IsCanceled || invalidateTask.IsFaulted)
            {
                // Propagate cancellations or synchronous faults so callers can release pacing tickets immediately.
                invalidateTask.GetAwaiter().GetResult();
            }

            request.Complete();

            if (!invalidateTask.IsCompleted)
            {
                _ = invalidateTask.ContinueWith(
                    t => logger.Warning(t.Exception, "FramePump invalidate task faulted"),
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            else if (invalidateTask.IsFaulted)
            {
                logger.Warning(invalidateTask.Exception, "FramePump invalidate task faulted");
            }
        }
        catch (OperationCanceledException ex)
        {
            request.Fail(ex);
            throw;
        }
        catch (Exception ex)
        {
            request.Fail(ex);
            logger.Warning(ex, "FramePump failed to invalidate Chromium");
        }
    }

    private static Task DefaultInvalidateBrowserAsync(
        ChromiumWebBrowser browser,
        ILogger logger,
        CancellationToken token)
    {
        var host = browser.GetBrowserHost();
        if (host is null)
        {
            return Task.CompletedTask;
        }

        token.ThrowIfCancellationRequested();

        Task uiTask;

        try
        {
            uiTask = Cef.UIThreadTaskFactory.StartNew(() =>
            {
                try
                {
                    host.Invalidate(PaintElementType.View);
                }
                catch (Exception ex)
                {
                    logger.Warning(ex, "FramePump invalidate threw on UI thread");
                }
            });
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "FramePump failed to queue invalidate on UI thread");
            throw;
        }

        if (uiTask.IsFaulted)
        {
            logger.Warning(uiTask.Exception, "FramePump invalidate task faulted");
            return Task.CompletedTask;
        }

        if (!uiTask.IsCompleted)
        {
            _ = uiTask.ContinueWith(
                t =>
                {
                    if (t.IsFaulted)
                    {
                        logger.Warning(t.Exception, "FramePump invalidate task faulted");
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        return Task.CompletedTask;
    }

    private async Task RunPeriodicLoopAsync(CancellationToken token)
    {
        var stopwatch = Stopwatch.StartNew();
        var nextDeadline = stopwatch.Elapsed;

        try
        {
            while (!token.IsCancellationRequested)
            {
                var interval = GetAdaptiveInterval();
                if (interval <= TimeSpan.Zero)
                {
                    interval = TimeSpan.FromMilliseconds(1);
                }

                nextDeadline += interval;
                var delay = nextDeadline - stopwatch.Elapsed;
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

                try
                {
                    await RequestInvalidateAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
            }
        }
        finally
        {
            stopwatch.Stop();
        }
    }

    private async Task RunWatchdogAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(watchdogInterval, token).ConfigureAwait(false);
                }
                catch (TaskCanceledException)
                {
                    return;
                }

                var lastPaint = new DateTime(Interlocked.Read(ref lastPaintTicks), DateTimeKind.Utc);
                if (DateTime.UtcNow - lastPaint > watchdogInterval)
                {
                    if (paused)
                    {
                        continue;
                    }

                    logger.Debug(
                        "FramePump watchdog triggering invalidate after {Seconds}s idle",
                        watchdogInterval.TotalSeconds);

                    try
                    {
                        await RequestInvalidateAsync(token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch (ObjectDisposedException)
                    {
                        return;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private TimeSpan GetAdaptiveInterval()
    {
        if (!cadenceAdaptationEnabled)
        {
            return baseInterval;
        }

        var delta = Volatile.Read(ref cadenceAlignmentDeltaFrames);
        if (double.IsNaN(delta))
        {
            return baseInterval;
        }

        var scaled = Math.Clamp(delta * CadenceAdaptationGain, -MaxCadenceAdjustmentFrames, MaxCadenceAdjustmentFrames);
        var adjustmentTicks = (long)(scaled * baseInterval.Ticks);
        var adjustedTicks = baseInterval.Ticks + adjustmentTicks;
        if (adjustedTicks < 1)
        {
            adjustedTicks = 1;
        }

        return TimeSpan.FromTicks(adjustedTicks);
    }

    private async Task ApplyOnDemandCadenceDelayAsync(CancellationToken token)
    {
        if (!cadenceAdaptationEnabled)
        {
            return;
        }

        var delta = Volatile.Read(ref cadenceAlignmentDeltaFrames);
        if (double.IsNaN(delta) || delta <= 0)
        {
            return;
        }

        var scaled = Math.Clamp(delta * CadenceAdaptationGain, 0, MaxCadenceAdjustmentFrames);
        if (scaled <= 0)
        {
            return;
        }

        var delayTicks = (long)Math.Clamp(scaled * baseInterval.Ticks, 0, baseInterval.Ticks * MaxCadenceAdjustmentFrames);
        if (delayTicks <= 0)
        {
            return;
        }

        var delay = TimeSpan.FromTicks(delayTicks);
        try
        {
            await Task.Delay(delay, token).ConfigureAwait(false);
        }
        catch (TaskCanceledException)
        {
        }
    }

    private void ThrowIfDisposed()
    {
        if (disposed)
        {
            throw new ObjectDisposedException(nameof(FramePump));
        }
    }

    private Task StartDedicatedTask(Func<CancellationToken, Task> worker)
    {
        return Task.Factory.StartNew(
                () => worker(cancellation.Token),
                CancellationToken.None,
                TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default)
            .Unwrap();
    }

    private sealed class InvalidationRequest : IDisposable
    {
        private readonly TaskCompletionSource<bool> completionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationToken cancellationToken;
        private readonly CancellationTokenRegistration registration;

        public InvalidationRequest(CancellationToken cancellationToken)
        {
            this.cancellationToken = cancellationToken;
            if (cancellationToken.CanBeCanceled)
            {
                registration = cancellationToken.Register(() => completionSource.TrySetCanceled(cancellationToken));
            }
        }

        public Task Completion => completionSource.Task;

        public bool IsCancellationRequested => cancellationToken.IsCancellationRequested;

        public void Complete() => completionSource.TrySetResult(true);

        public void Fail(Exception ex) => completionSource.TrySetException(ex);

        public void Dispose() => registration.Dispose();
    }
}
