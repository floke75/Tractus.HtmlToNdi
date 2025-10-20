namespace Tractus.HtmlToNdi.Video;

/// <summary>
/// A helper class to calculate the average frame time over a sliding window.
/// </summary>
internal sealed class FrameTimeAverager
{
    private readonly int capacity;
    private readonly Queue<double> samples;
    private double sum;
    private DateTime? lastTimestamp;

    /// <summary>
    /// Initializes a new instance of the <see cref="FrameTimeAverager"/> class.
    /// </summary>
    /// <param name="capacity">The capacity of the sliding window.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if the capacity is not positive.</exception>
    public FrameTimeAverager(int capacity = 60)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        this.capacity = capacity;
        this.samples = new Queue<double>(capacity);
    }

    /// <summary>
    /// Adds a timestamp to the averager and returns the current average frames per second.
    /// </summary>
    /// <param name="timestamp">The timestamp to add.</param>
    /// <returns>The average frames per second, or null if there are not enough samples.</returns>
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
