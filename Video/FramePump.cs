using System;
using System.Threading;

using System.Threading.Tasks;

using Serilog;

namespace Tractus.HtmlToNdi.Video;

public sealed class FramePump : IAsyncDisposable
{
    private readonly FrameRate _targetRate;
    private readonly Func<ValueTask> _invalidate;
    private readonly PeriodicTimer _timer;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _pumpTask;
    private readonly TimeSpan _watchdogInterval;
    private readonly ILogger _logger;
    private long _lastDeliveredTicks;

    public FramePump(FrameRate targetRate, Func<ValueTask> invalidate, ILogger logger, TimeSpan? watchdogInterval = null)
    {
        _targetRate = targetRate;
        _invalidate = invalidate ?? throw new ArgumentNullException(nameof(invalidate));
        _logger = logger;
        _watchdogInterval = watchdogInterval ?? TimeSpan.FromSeconds(1);
        _timer = new PeriodicTimer(targetRate.FrameDuration);
        _lastDeliveredTicks = DateTime.UtcNow.Ticks;
        _pumpTask = Task.Run(PumpAsync);
    }

    public void NotifyFrameDelivered()
    {
        Interlocked.Exchange(ref _lastDeliveredTicks, DateTime.UtcNow.Ticks);
    }

    private async Task PumpAsync()
    {
        try
        {
            while (await _timer.WaitForNextTickAsync(_cts.Token).ConfigureAwait(false))
            {
                await _invalidate().ConfigureAwait(false);

                var last = new DateTime(Interlocked.Read(ref _lastDeliveredTicks), DateTimeKind.Utc);
                if (DateTime.UtcNow - last >= _watchdogInterval)
                {
                    _logger.Debug("FramePump watchdog firing additional invalidate after {Elapsed:0.00}s without delivery", (DateTime.UtcNow - last).TotalSeconds);
                    await _invalidate().ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "FramePump encountered an unexpected exception");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _timer.Dispose();
        try
        {
            await _pumpTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "FramePump failed while disposing");
        }
        finally
        {
            _cts.Dispose();
        }
    }
}
