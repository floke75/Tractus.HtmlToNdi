using System;
using System.Diagnostics;
using System.Threading;

namespace Tractus.HtmlToNdi.FramePacing;

public sealed class FramePacer : IDisposable
{
    private readonly FramePacerEngine engine;
    private readonly CancellationTokenSource cancellationTokenSource = new();
    private Thread? workerThread;
    private bool disposed;

    public FramePacer(FramePacerEngine engine)
    {
        this.engine = engine;
    }

    public FramePacer(FrameRingBuffer<BrowserFrame> buffer, FrameRate frameRate, Action<BrowserFrame, FrameDeliveryContext> sendFrame, Serilog.ILogger logger)
        : this(new FramePacerEngine(buffer, frameRate, sendFrame, logger))
    {
    }

    public FrameRate FrameRate => this.engine.FrameRate;

    public void Start()
    {
        if (this.workerThread != null)
        {
            return;
        }

        this.workerThread = new Thread(this.RunLoop)
        {
            IsBackground = true,
            Name = "FramePacer",
            Priority = ThreadPriority.AboveNormal,
        };
        this.workerThread.Start();
    }

    public FramePacerMetrics GetMetricsSnapshot() => this.engine.GetMetricsSnapshot();

    private void RunLoop()
    {
        var token = this.cancellationTokenSource.Token;
        var interval = this.engine.Interval;
        var stopwatch = Stopwatch.StartNew();
        var nextTick = stopwatch.Elapsed;

        while (!token.IsCancellationRequested)
        {
            var remaining = nextTick - stopwatch.Elapsed;
            if (remaining > TimeSpan.Zero)
            {
                if (remaining > TimeSpan.FromMilliseconds(2))
                {
                    Thread.Sleep(remaining - TimeSpan.FromMilliseconds(1));
                }
                else
                {
                    SpinWait.SpinUntil(() => stopwatch.Elapsed >= nextTick || token.IsCancellationRequested, remaining);
                }
            }
            else
            {
                nextTick = stopwatch.Elapsed;
            }

            if (token.IsCancellationRequested)
            {
                break;
            }

            this.engine.ProcessTick(DateTime.UtcNow);
            nextTick += interval;
        }
    }

    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.cancellationTokenSource.Cancel();
        this.workerThread?.Join();
        this.workerThread = null;
        this.cancellationTokenSource.Dispose();
        this.disposed = true;
    }
}
