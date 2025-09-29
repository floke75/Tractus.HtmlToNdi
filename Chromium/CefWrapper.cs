using CefSharp;
using CefSharp.OffScreen;
using NewTek;
using Serilog;

namespace Tractus.HtmlToNdi.Chromium;

public class CefWrapper : IDisposable
{
    private bool disposedValue;
    private ChromiumWebBrowser? browser;
    private readonly FrameRingBuffer frameBuffer;
    private readonly FramePacer framePacer;
    private readonly FramePacingOptions pacingOptions;
    private readonly object metricsLock = new();
    private double intervalSum;
    private double intervalMin = double.MaxValue;
    private double intervalMax;
    private int intervalCount;
    private long repeatedSinceLastLog;
    private long droppedSinceLastLog;
    private DateTimeOffset lastMetricsLog = DateTimeOffset.UtcNow;

    public int Width { get; private set; }
    public int Height { get; private set; }

    public string? Url { get; private set; }

    private Thread RenderWatchdog;
    private DateTime lastPaint = DateTime.MinValue;

    public CefWrapper(int width, int height, string initialUrl, FramePacingOptions pacingOptions)
    {
        this.Width = width;
        this.Height = height;
        this.Url = initialUrl;
        this.pacingOptions = pacingOptions;

        this.frameBuffer = new FrameRingBuffer(pacingOptions.BufferDepth);
        this.framePacer = new FramePacer(this.frameBuffer, pacingOptions);
        this.framePacer.FrameReady += this.OnFrameReady;

        this.browser = new ChromiumWebBrowser(initialUrl)
        {
            AudioHandler = new CustomAudioHandler(),
        };

        this.browser.Size = new System.Drawing.Size(this.Width, this.Height);

        this.RenderWatchdog = new Thread(this.RenderWatchDogThread)
        {
            IsBackground = true,
            Name = "CEF Render Watchdog"
        };
    }

    private void RenderWatchDogThread()
    {
        while (!this.disposedValue)
        {
            if (this.browser is not null && DateTime.Now.Subtract(this.lastPaint).TotalSeconds >= 1.0)
            {
                this.browser.GetBrowser().GetHost().Invalidate(PaintElementType.View);
            }

            Thread.Sleep(1000);
        }
    }

    public async Task InitializeWrapperAsync()
    {
        if (this.browser is null)
        {
            return;
        }

        await this.browser.WaitForInitialLoadAsync();

        this.browser.GetBrowserHost().WindowlessFrameRate = this.pacingOptions.WindowlessFrameRate;
        this.browser.ToggleAudioMute();

        this.browser.Paint += this.OnBrowserPaint;
        this.RenderWatchdog.Start();
        this.framePacer.Start();
    }

    private void OnBrowserPaint(object? sender, OnPaintEventArgs e)
    {
        if (this.browser is null)
        {
            return;
        }

        if (e.Width == 0 || e.Height == 0)
        {
            return;
        }

        this.lastPaint = DateTime.Now;

        try
        {
            var stride = e.Width * 4;
            var frame = FrameData.Create(e.BufferHandle, e.Width, e.Height, stride, DateTimeOffset.UtcNow);
            this.frameBuffer.Enqueue(frame);
        }
        catch (Exception ex)
        {
            Log.Logger.Error(ex, "Failed to enqueue frame for pacing");
        }
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!this.disposedValue)
        {
            if (disposing)
            {
                this.framePacer.FrameReady -= this.OnFrameReady;
                this.framePacer.Dispose();
                this.frameBuffer.Dispose();
                if (this.browser is not null)
                {
                    this.browser.Paint -= this.OnBrowserPaint;
                    this.browser.Dispose();
                }

                this.browser = null;
            }

            this.disposedValue = true;
        }
    }

    public void Dispose()
    {
        this.Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    public void SetUrl(string url)
    {
        if (this.browser is null)
        {
            return;
        }

        this.Url = url;

        this.browser.Load(url);
    }

    public void ScrollBy(int increment)
    {
        this.browser?.SendMouseWheelEvent(0, 0, 0, increment, CefEventFlags.None);
    }

    public void Click(int x, int y)
    {
        var host = this.browser?.GetBrowser()?.GetHost();

        if (host is null)
        {
            return;
        }

        host.SendMouseClickEvent(x, y,
            MouseButtonType.Left, false, 1, CefEventFlags.None);
        Thread.Sleep(100);
        host.SendMouseClickEvent(x, y,
            MouseButtonType.Left, true, 1, CefEventFlags.None);
    }

    public void SendKeystrokes(Models.SendKeystrokeModel model)
    {
        var host = this.browser?.GetBrowser()?.GetHost();

        if (host is null)
        {
            return;
        }

        foreach (var c in model.ToSend)
        {
            host.SendKeyEvent(new KeyEvent()
            {
                Type = KeyEventType.KeyDown,
                NativeKeyCode = Convert.ToInt32(c)
            });
        }
    }

    public void RefreshPage()
    {
        this.browser?.Reload();
    }

    private void OnFrameReady(FrameDispatchResult dispatchResult)
    {
        if (Program.NdiSenderPtr == nint.Zero)
        {
            return;
        }

        dispatchResult.Frame.WithPointer(pointer =>
        {
            var videoFrame = new NDIlib.video_frame_v2_t()
            {
                FourCC = NDIlib.FourCC_type_e.FourCC_type_BGRA,
                frame_rate_N = this.pacingOptions.FrameRateNumerator,
                frame_rate_D = this.pacingOptions.FrameRateDenominator,
                frame_format_type = NDIlib.frame_format_type_e.frame_format_type_progressive,
                line_stride_in_bytes = dispatchResult.Frame.Stride,
                picture_aspect_ratio = (float)dispatchResult.Frame.Width / dispatchResult.Frame.Height,
                p_data = pointer,
                timecode = NDIlib.send_timecode_synthesize,
                xres = dispatchResult.Frame.Width,
                yres = dispatchResult.Frame.Height,
            };

            NDIlib.send_send_video_v2(Program.NdiSenderPtr, ref videoFrame);
        });

        this.UpdateMetrics(dispatchResult);
    }

    private void UpdateMetrics(FrameDispatchResult dispatchResult)
    {
        lock (this.metricsLock)
        {
            if (dispatchResult.ActualInterval > TimeSpan.Zero)
            {
                var intervalMs = dispatchResult.ActualInterval.TotalMilliseconds;
                this.intervalSum += intervalMs;
                this.intervalMin = this.intervalCount == 0 ? intervalMs : Math.Min(this.intervalMin, intervalMs);
                this.intervalMax = this.intervalCount == 0 ? intervalMs : Math.Max(this.intervalMax, intervalMs);
                this.intervalCount++;
            }

            if (dispatchResult.IsRepeat)
            {
                this.repeatedSinceLastLog++;
            }

            if (dispatchResult.DroppedFrames > 0)
            {
                this.droppedSinceLastLog += dispatchResult.DroppedFrames;
            }

            var now = DateTimeOffset.UtcNow;
            if (now - this.lastMetricsLog >= TimeSpan.FromSeconds(5) && this.intervalCount > 0)
            {
                var avg = this.intervalSum / this.intervalCount;
                Log.Logger.Information(
                    "Frame pacing stats: target={TargetFps:F3}fps interval={TargetInterval:F3}ms avg={AverageInterval:F3}ms min={MinInterval:F3}ms max={MaxInterval:F3}ms repeats={RepeatCount} drops={DropCount}",
                    this.pacingOptions.TargetFrameRate,
                    this.pacingOptions.TargetInterval.TotalMilliseconds,
                    avg,
                    this.intervalMin,
                    this.intervalMax,
                    this.repeatedSinceLastLog,
                    this.droppedSinceLastLog);

                this.intervalSum = 0;
                this.intervalCount = 0;
                this.intervalMin = double.MaxValue;
                this.intervalMax = 0;
                this.repeatedSinceLastLog = 0;
                this.droppedSinceLastLog = 0;
                this.lastMetricsLog = now;
            }
        }
    }
}
