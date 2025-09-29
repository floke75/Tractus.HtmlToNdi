using System;

namespace Tractus.HtmlToNdi.Video;

public sealed class FrameTimeAverager
{
    private readonly double[] _samples;
    private int _index;
    private int _count;
    private DateTime? _lastTimestamp;
    private readonly object _gate = new();

    public FrameTimeAverager(int sampleCount = 120)
    {
        if (sampleCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleCount));
        }

        _samples = new double[sampleCount];
    }

    public void Observe(DateTime timestampUtc)
    {
        lock (_gate)
        {
            if (_lastTimestamp is DateTime last)
            {
                var delta = (timestampUtc - last).TotalSeconds;
                if (delta > 0 && delta < 1)
                {
                    _samples[_index] = delta;
                    _index = (_index + 1) % _samples.Length;
                    if (_count < _samples.Length)
                    {
                        _count++;
                    }
                }
            }

            _lastTimestamp = timestampUtc;
        }
    }

    public FrameRate GetFrameRate(FrameRate fallback)
    {
        lock (_gate)
        {
            if (_count == 0)
            {
                return fallback;
            }

            double total = 0;
            for (var i = 0; i < _count; i++)
            {
                total += _samples[i];
            }

            var average = total / _count;
            if (average <= 0)
            {
                return fallback;
            }

            var fps = 1.0 / average;
            return FrameRate.FromDouble(fps, fallback);
        }
    }
}
