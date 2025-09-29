using CefSharp;
using CefSharp.OffScreen;
using Serilog;

namespace Tractus.HtmlToNdi.Video;

/// <summary>
/// Periodically invalidates Chromium to maintain the requested cadence.
/// </summary>
public sealed class FramePump : IDisposable
{
    private readonly FrameRate _targetRate;
    private readonly ILogger _logger;
    private readonly TimeSpan _watchdogInterval;
    private CancellationTokenSource? _cts;
    private Task? _pumpTask;
    private ChromiumWebBrowser? _browser;
    private DateTime _lastInvalidate = DateTime.UtcNow;

    public FramePump(FrameRate targetRate, TimeSpan watchdogInterval, ILogger logger)
    {
        _targetRate = targetRate;
        _watchdogInterval = watchdogInterval;
        _logger = logger;
    }

    public void Attach(ChromiumWebBrowser browser)
    {
        _browser = browser;
    }

    public void Start()
    {
        if (_pumpTask != null)
        {
            return;
        }

        if (_browser == null)
        {
            throw new InvalidOperationException("Browser must be attached before starting the frame pump.");
        }

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _pumpTask = Task.Run(async () =>
        {
            try
            {
                using var timer = new PeriodicTimer(_targetRate.FrameInterval);
                while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                {
                    PumpOnce();
                }
            }
            catch (OperationCanceledException)
            {
                // ignored
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Frame pump crashed.");
            }
        }, token);
    }

    public void PumpOnce()
    {
        var browser = _browser;
        if (browser == null)
        {
            return;
        }

        try
        {
            browser.GetBrowserHost()?.Invalidate(PaintElementType.View);
            _lastInvalidate = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to invalidate Chromium frame.");
        }
    }

    public void WatchdogTick()
    {
        if (DateTime.UtcNow - _lastInvalidate > _watchdogInterval)
        {
            _logger.Warning("Frame pump watchdog detected inactivity; forcing invalidate.");
            PumpOnce();
        }
    }

    public async Task StopAsync()
    {
        if (_cts == null || _pumpTask == null)
        {
            return;
        }

        _cts.Cancel();
        try
        {
            await _pumpTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            _pumpTask = null;
        }
    }

    public void Dispose()
    {
        _ = StopAsync();
    }
}
