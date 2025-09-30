using Serilog;

namespace Tractus.HtmlToNdi.Video;

public enum FramePacerDecision
{
    Fresh,
    RepeatLast,
}

public interface IFrameProvider
{
    bool TryTakeLatest(out BufferedVideoFrame frame, out int discarded);
}

public interface IFrameConsumer
{
    void OnFrame(BufferedVideoFrame? frame, FramePacerDecision decision, int discarded);
}

/// <summary>
/// Drives a consumer at a fixed cadence, fetching frames from the provider.
/// </summary>
public sealed class FramePacer : IDisposable
{
    private readonly FrameRate _frameRate;
    private readonly IFrameProvider _provider;
    private readonly IFrameConsumer _consumer;
    private readonly ILogger _logger;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public FramePacer(FrameRate frameRate, IFrameProvider provider, IFrameConsumer consumer, ILogger logger)
    {
        _frameRate = frameRate;
        _provider = provider;
        _consumer = consumer;
        _logger = logger;
    }

    public void Start()
    {
        if (_loop != null)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _loop = Task.Run(async () =>
        {
            try
            {
                using var timer = new PeriodicTimer(_frameRate.FrameInterval);
                while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                {
                    ProcessTick();
                }
            }
            catch (OperationCanceledException)
            {
                // ignored
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Frame pacer loop crashed.");
            }
        }, token);
    }

    public void ProcessTick()
    {
        if (_provider.TryTakeLatest(out var frame, out var discarded))
        {
            _consumer.OnFrame(frame, FramePacerDecision.Fresh, discarded);
        }
        else
        {
            _consumer.OnFrame(null, FramePacerDecision.RepeatLast, 0);
        }
    }

    public async Task StopAsync()
    {
        if (_cts == null || _loop == null)
        {
            return;
        }

        _cts.Cancel();
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // ignored
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            _loop = null;
        }
    }

    public void Dispose()
    {
        _ = StopAsync();
    }
}
