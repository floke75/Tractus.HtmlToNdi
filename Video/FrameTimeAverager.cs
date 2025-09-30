namespace Tractus.HtmlToNdi.Video;

/// <summary>
/// Exponentially-weighted moving average of frame durations.
/// </summary>
public sealed class FrameTimeAverager
{
    private readonly double _smoothing;
    private double? _avgFrameSeconds;
    private DateTime? _lastSample;

    public FrameTimeAverager(double smoothing = 0.15)
    {
        if (smoothing <= 0 || smoothing >= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(smoothing));
        }

        _smoothing = smoothing;
    }

    public void RecordFrame(DateTime timestampUtc)
    {
        if (_lastSample is null)
        {
            _lastSample = timestampUtc;
            return;
        }

        var delta = (timestampUtc - _lastSample.Value).TotalSeconds;
        _lastSample = timestampUtc;
        if (delta <= 0)
        {
            return;
        }

        if (_avgFrameSeconds is null)
        {
            _avgFrameSeconds = delta;
        }
        else
        {
            _avgFrameSeconds = (_smoothing * delta) + ((1 - _smoothing) * _avgFrameSeconds.Value);
        }
    }

    public FrameRate GetFrameRateOr(FrameRate fallback)
    {
        if (_avgFrameSeconds is null)
        {
            return fallback;
        }

        var fps = 1.0 / _avgFrameSeconds.Value;
        return FrameRate.FromDouble(fps);
    }
}
