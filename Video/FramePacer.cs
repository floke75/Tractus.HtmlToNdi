using System;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace Tractus.HtmlToNdi.Video;

public sealed class FramePacer : IAsyncDisposable, IDisposable
{
    private readonly FrameRingBuffer buffer;
    private readonly IVideoFrameSender sender;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan interval;
    private readonly CancellationTokenSource cts = new();
    private Task? pacingTask;

    private long lastReadSequence;
    private VideoFrameData? lastFrame;
    private DateTime lastSendUtc = DateTime.MinValue;
    private double minIntervalMs = double.MaxValue;
    private double maxIntervalMs = double.MinValue;
    private long totalFramesSent;
    private long totalRepeats;
    private long totalDrops;

    public FramePacer(FrameRingBuffer buffer, IVideoFrameSender sender, double targetFps, TimeProvider? timeProvider = null)
    {
        if (targetFps <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetFps));
        }

        this.buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
        this.sender = sender ?? throw new ArgumentNullException(nameof(sender));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.interval = TimeSpan.FromSeconds(1.0 / targetFps);
    }

    public void Start()
    {
        if (this.pacingTask is not null)
        {
            return;
        }

        this.pacingTask = Task.Run(this.RunAsync, this.cts.Token);
    }

    private async Task RunAsync()
    {
        var timer = this.timeProvider.CreatePeriodicTimer(this.interval);
        try
        {
            while (!this.cts.IsCancellationRequested)
            {
                if (!await timer.WaitForNextTickAsync(this.cts.Token))
                {
                    break;
                }

                await this.TickAsync();
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            await timer.DisposeAsync();
        }
    }

    private Task TickAsync()
    {
        var hasNewFrame = this.buffer.TryGetLatest(ref this.lastReadSequence, out var nextFrame, out var droppedFrames);
        var repeatFrame = false;
        if (hasNewFrame && nextFrame is not null)
        {
            this.lastFrame = nextFrame;
            if (droppedFrames > 0)
            {
                this.totalDrops += droppedFrames;
            }
        }
        else if (this.lastFrame is not null)
        {
            repeatFrame = true;
            this.totalRepeats++;
        }
        else
        {
            return Task.CompletedTask;
        }

        var nowUtc = this.timeProvider.GetUtcNow().UtcDateTime;
        if (this.lastSendUtc != DateTime.MinValue)
        {
            var delta = nowUtc - this.lastSendUtc;
            var deltaMs = delta.TotalMilliseconds;
            this.minIntervalMs = Math.Min(this.minIntervalMs, deltaMs);
            this.maxIntervalMs = Math.Max(this.maxIntervalMs, deltaMs);
            Log.Logger.Debug("Pacer tick Δ={Interval:F3}ms repeat={IsRepeat} dropped={Dropped} published={Sequence}",
                deltaMs,
                repeatFrame,
                droppedFrames,
                this.lastReadSequence);
        }

        this.lastSendUtc = nowUtc;
        this.totalFramesSent++;

        this.sender.Send(this.lastFrame!, repeatFrame, droppedFrames, this.interval);

        if (this.totalFramesSent % 60 == 0)
        {
            var jitter = this.maxIntervalMs - this.minIntervalMs;
            Log.Logger.Information(
                "Pacer stats fpsTarget={TargetFps:F3} frames={Frames} repeats={Repeats} drops={Drops} jitter={Jitter:F3}ms",
                1.0 / this.interval.TotalSeconds,
                this.totalFramesSent,
                this.totalRepeats,
                this.totalDrops,
                jitter);
            this.minIntervalMs = double.MaxValue;
            this.maxIntervalMs = double.MinValue;
        }

        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        this.cts.Cancel();
        if (this.pacingTask is not null)
        {
            try
            {
                await this.pacingTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        this.cts.Dispose();
    }

    public void Dispose()
    {
        this.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
