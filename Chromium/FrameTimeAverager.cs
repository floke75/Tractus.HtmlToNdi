using System;
using System.Diagnostics;
using System.Threading;

namespace Tractus.HtmlToNdi.Chromium;

internal sealed class FrameTimeAverager
{
    private readonly object gate = new();
    private readonly double[] samples;
    private readonly int capacity;
    private readonly double tickLength;

    private int nextIndex;
    private int filledSamples;
    private double accumulatedSeconds;
    private long lastTimestamp;

    public FrameTimeAverager(int sampleCount)
    {
        if (sampleCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleCount));
        }

        this.capacity = sampleCount;
        this.samples = new double[sampleCount];
        this.tickLength = 1.0 / Stopwatch.Frequency;
    }

    public double? RegisterFrame()
    {
        var now = Stopwatch.GetTimestamp();
        var previous = Interlocked.Exchange(ref this.lastTimestamp, now);

        if (previous == 0)
        {
            return null;
        }

        var deltaTicks = now - previous;
        if (deltaTicks <= 0)
        {
            return null;
        }

        var deltaSeconds = deltaTicks * this.tickLength;
        lock (this.gate)
        {
            if (this.filledSamples == this.capacity)
            {
                this.accumulatedSeconds -= this.samples[this.nextIndex];
            }
            else
            {
                this.filledSamples++;
            }

            this.samples[this.nextIndex] = deltaSeconds;
            this.accumulatedSeconds += deltaSeconds;
            this.nextIndex = (this.nextIndex + 1) % this.capacity;

            var averageSeconds = this.accumulatedSeconds / this.filledSamples;
            if (averageSeconds <= 0)
            {
                return null;
            }

            return 1.0 / averageSeconds;
        }
    }

    public TimeSpan TimeSinceLastFrame
    {
        get
        {
            var last = Volatile.Read(ref this.lastTimestamp);
            if (last == 0)
            {
                return TimeSpan.MaxValue;
            }

            var deltaTicks = Stopwatch.GetTimestamp() - last;
            if (deltaTicks <= 0)
            {
                return TimeSpan.Zero;
            }

            return TimeSpan.FromSeconds(deltaTicks * this.tickLength);
        }
    }

    public void Reset()
    {
        lock (this.gate)
        {
            Array.Clear(this.samples, 0, this.samples.Length);
            this.nextIndex = 0;
            this.filledSamples = 0;
            this.accumulatedSeconds = 0;
            Volatile.Write(ref this.lastTimestamp, 0);
        }
    }
}