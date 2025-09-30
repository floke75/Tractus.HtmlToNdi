using System;
using System.Threading;

namespace Tractus.HtmlToNdi.Video;

public sealed class FrameTimeAverager
{
    private readonly double[] durations;
    private int index;
    private int count;
    private long lastTimestampTicks;
    private readonly object sync = new();

    public FrameTimeAverager(int sampleCount = 120)
    {
        if (sampleCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleCount));
        }

        durations = new double[sampleCount];
    }

    public void AddSample(DateTime timestampUtc)
    {
        var previousTicks = Interlocked.Exchange(ref lastTimestampTicks, timestampUtc.Ticks);
        if (previousTicks == 0)
        {
            return;
        }

        var deltaSeconds = new TimeSpan(timestampUtc.Ticks - previousTicks).TotalSeconds;
        if (deltaSeconds <= 0)
        {
            return;
        }

        lock (sync)
        {
            durations[index] = deltaSeconds;
            index = (index + 1) % durations.Length;
            if (count < durations.Length)
            {
                count++;
            }
        }
    }

    public double GetAverageFps()
    {
        lock (sync)
        {
            if (count == 0)
            {
                return 0;
            }

            var sum = 0d;
            for (var i = 0; i < count; i++)
            {
                sum += durations[i];
            }

            var averageSeconds = sum / count;
            return averageSeconds > 0 ? 1d / averageSeconds : 0;
        }
    }

    public void Reset()
    {
        lock (sync)
        {
            Array.Clear(durations);
            index = 0;
            count = 0;
            Interlocked.Exchange(ref lastTimestampTicks, 0);
        }
    }
}
