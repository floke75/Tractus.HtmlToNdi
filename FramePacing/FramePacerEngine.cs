using Serilog;

namespace Tractus.HtmlToNdi.FramePacing;

internal sealed class FramePacerEngine
{
    private static readonly TimeSpan MetricsLogInterval = TimeSpan.FromSeconds(1);

    private readonly FrameRingBuffer<BrowserFrame> buffer;
    private readonly Action<BrowserFrame, FrameDeliveryContext> sendFrame;
    private readonly ILogger logger;

    private readonly TimeSpan interval;

    private BrowserFrame? lastFrame;
    private long lastSequence = -1;
    private DateTime? lastTickTime;
    private DateTime lastMetricsLog = DateTime.MinValue;

    private long totalFramesSent;
    private long totalRepeats;
    private long totalDrops;

    private TimeSpan totalIntervalSum = TimeSpan.Zero;
    private long totalIntervalCount;
    private TimeSpan totalIntervalMin = TimeSpan.MaxValue;
    private TimeSpan totalIntervalMax = TimeSpan.Zero;

    private TimeSpan totalLatencySum = TimeSpan.Zero;
    private long totalLatencyCount;
    private TimeSpan totalLatencyMin = TimeSpan.MaxValue;
    private TimeSpan totalLatencyMax = TimeSpan.Zero;

    private TimeSpan logIntervalSum = TimeSpan.Zero;
    private long logIntervalCount;
    private TimeSpan logIntervalMin = TimeSpan.MaxValue;
    private TimeSpan logIntervalMax = TimeSpan.Zero;

    private TimeSpan logLatencySum = TimeSpan.Zero;
    private long logLatencyCount;
    private TimeSpan logLatencyMin = TimeSpan.MaxValue;
    private TimeSpan logLatencyMax = TimeSpan.Zero;

    private long logRepeats;
    private long logDrops;

    public FramePacerEngine(
        FrameRingBuffer<BrowserFrame> buffer,
        FrameRate frameRate,
        Action<BrowserFrame, FrameDeliveryContext> sendFrame,
        ILogger logger)
    {
        this.buffer = buffer;
        this.FrameRate = frameRate;
        this.interval = TimeSpan.FromSeconds(frameRate.Denominator / (double)frameRate.Numerator);
        this.sendFrame = sendFrame;
        this.logger = logger;
    }

    public FrameRate FrameRate { get; }

    public TimeSpan Interval => this.interval;

    public void ProcessTick(DateTime tickTime)
    {
        var backlog = this.buffer.GetBacklog(this.lastSequence);
        this.UpdateIntervalStats(tickTime);

        var sentFrame = false;
        var repeated = false;

        if (this.buffer.TryGetLatest(ref this.lastSequence, out var frame, out var dropped))
        {
            this.lastFrame = frame;
            this.totalDrops += dropped;
            this.logDrops += dropped;

            var latency = tickTime - frame.CapturedAt;
            this.RecordLatency(latency);

            this.InvokeSend(frame, new FrameDeliveryContext(false, dropped, latency, backlog));
            sentFrame = true;
        }
        else if (this.lastFrame.HasValue)
        {
            var frameToRepeat = this.lastFrame.Value;
            var latency = tickTime - frameToRepeat.CapturedAt;
            this.InvokeSend(frameToRepeat, new FrameDeliveryContext(true, 0, latency, backlog));
            this.totalRepeats++;
            this.logRepeats++;
            sentFrame = true;
            repeated = true;
        }

        if (sentFrame)
        {
            this.totalFramesSent++;
        }

        this.MaybeLog(tickTime, backlog, repeated);
    }

    public FramePacerMetrics GetMetricsSnapshot()
    {
        double? avgInterval = this.totalIntervalCount > 0
            ? this.totalIntervalSum.TotalMilliseconds / this.totalIntervalCount
            : null;
        double? minInterval = this.totalIntervalCount > 0 && this.totalIntervalMin != TimeSpan.MaxValue
            ? this.totalIntervalMin.TotalMilliseconds
            : null;
        double? maxInterval = this.totalIntervalCount > 0 && this.totalIntervalMax != TimeSpan.Zero
            ? this.totalIntervalMax.TotalMilliseconds
            : null;

        double? avgLatency = this.totalLatencyCount > 0
            ? this.totalLatencySum.TotalMilliseconds / this.totalLatencyCount
            : null;
        double? minLatency = this.totalLatencyCount > 0 && this.totalLatencyMin != TimeSpan.MaxValue
            ? this.totalLatencyMin.TotalMilliseconds
            : null;
        double? maxLatency = this.totalLatencyCount > 0 && this.totalLatencyMax != TimeSpan.Zero
            ? this.totalLatencyMax.TotalMilliseconds
            : null;

        return new FramePacerMetrics(
            this.totalFramesSent,
            this.totalRepeats,
            this.totalDrops,
            avgInterval,
            minInterval,
            maxInterval,
            avgLatency,
            minLatency,
            maxLatency,
            this.FrameRate.FramesPerSecond,
            this.buffer.Capacity);
    }

    private void UpdateIntervalStats(DateTime tickTime)
    {
        if (this.lastTickTime.HasValue)
        {
            var delta = tickTime - this.lastTickTime.Value;
            if (delta < TimeSpan.Zero)
            {
                delta = TimeSpan.Zero;
            }

            this.totalIntervalSum += delta;
            this.totalIntervalCount++;
            if (delta < this.totalIntervalMin)
            {
                this.totalIntervalMin = delta;
            }

            if (delta > this.totalIntervalMax)
            {
                this.totalIntervalMax = delta;
            }

            this.logIntervalSum += delta;
            this.logIntervalCount++;
            if (delta < this.logIntervalMin)
            {
                this.logIntervalMin = delta;
            }

            if (delta > this.logIntervalMax)
            {
                this.logIntervalMax = delta;
            }
        }

        this.lastTickTime = tickTime;
    }

    private void RecordLatency(TimeSpan latency)
    {
        if (latency < TimeSpan.Zero)
        {
            latency = TimeSpan.Zero;
        }

        this.totalLatencySum += latency;
        this.totalLatencyCount++;
        if (latency < this.totalLatencyMin)
        {
            this.totalLatencyMin = latency;
        }

        if (latency > this.totalLatencyMax)
        {
            this.totalLatencyMax = latency;
        }

        this.logLatencySum += latency;
        this.logLatencyCount++;
        if (latency < this.logLatencyMin)
        {
            this.logLatencyMin = latency;
        }

        if (latency > this.logLatencyMax)
        {
            this.logLatencyMax = latency;
        }
    }

    private void MaybeLog(DateTime tickTime, int backlog, bool repeated)
    {
        if ((tickTime - this.lastMetricsLog) < MetricsLogInterval)
        {
            return;
        }

        if (this.logIntervalCount == 0 && this.logLatencyCount == 0 && this.logDrops == 0 && this.logRepeats == 0)
        {
            this.lastMetricsLog = tickTime;
            return;
        }

        var avgInterval = this.logIntervalCount > 0
            ? this.logIntervalSum.TotalMilliseconds / this.logIntervalCount
            : (double?)null;
        var minInterval = this.logIntervalCount > 0 && this.logIntervalMin != TimeSpan.MaxValue
            ? this.logIntervalMin.TotalMilliseconds
            : (double?)null;
        var maxInterval = this.logIntervalCount > 0 && this.logIntervalMax != TimeSpan.Zero
            ? this.logIntervalMax.TotalMilliseconds
            : (double?)null;

        var avgLatency = this.logLatencyCount > 0
            ? this.logLatencySum.TotalMilliseconds / this.logLatencyCount
            : (double?)null;
        var minLatency = this.logLatencyCount > 0 && this.logLatencyMin != TimeSpan.MaxValue
            ? this.logLatencyMin.TotalMilliseconds
            : (double?)null;
        var maxLatency = this.logLatencyCount > 0 && this.logLatencyMax != TimeSpan.Zero
            ? this.logLatencyMax.TotalMilliseconds
            : (double?)null;

        this.logger.Debug("Frame pacer stats: target {TargetFps:F3} fps, avg interval {AverageInterval} ms (min {MinInterval} / max {MaxInterval}), avg latency {AverageLatency} ms, repeats {Repeats}, drops {Drops}, backlog {Backlog}, lastRepeat {LastRepeat}",
            this.FrameRate.FramesPerSecond,
            avgInterval,
            minInterval,
            maxInterval,
            avgLatency,
            this.logRepeats,
            this.logDrops,
            backlog,
            repeated);

        this.logIntervalSum = TimeSpan.Zero;
        this.logIntervalCount = 0;
        this.logIntervalMin = TimeSpan.MaxValue;
        this.logIntervalMax = TimeSpan.Zero;

        this.logLatencySum = TimeSpan.Zero;
        this.logLatencyCount = 0;
        this.logLatencyMin = TimeSpan.MaxValue;
        this.logLatencyMax = TimeSpan.Zero;

        this.logRepeats = 0;
        this.logDrops = 0;
        this.lastMetricsLog = tickTime;
    }

    private void InvokeSend(BrowserFrame frame, FrameDeliveryContext context)
    {
        if (!frame.HasBuffer)
        {
            return;
        }

        try
        {
            this.sendFrame(frame, context);
        }
        catch (Exception ex)
        {
            this.logger.Error(ex, "Frame send failed");
        }
    }
}
