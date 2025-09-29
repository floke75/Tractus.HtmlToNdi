namespace Tractus.HtmlToNdi.Video;

internal sealed class FrameTimeAverager
{
    private readonly int capacity;
    private readonly Queue<double> samples;
    private double sum;
    private DateTime? lastTimestamp;

    public FrameTimeAverager(int capacity = 60)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        this.capacity = capacity;
        this.samples = new Queue<double>(capacity);
    }

    public double? AddTimestamp(DateTime timestamp)
    {
        if (lastTimestamp.HasValue)
        {
            var delta = (timestamp - lastTimestamp.Value).TotalSeconds;
            if (delta > 0)
            {
                if (samples.Count == capacity)
                {
                    sum -= samples.Dequeue();
                }

                samples.Enqueue(delta);
                sum += delta;
            }
        }

        lastTimestamp = timestamp;

        if (samples.Count < 3)
        {
            return null;
        }

        var averageSeconds = sum / samples.Count;
        if (averageSeconds <= 0)
        {
            return null;
        }

        return 1.0 / averageSeconds;
    }
}
