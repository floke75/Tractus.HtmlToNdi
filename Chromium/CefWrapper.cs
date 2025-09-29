using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using CefSharp;
using CefSharp.OffScreen;
using NewTek;
using Serilog;
using Tractus.HtmlToNdi.Video;

namespace Tractus.HtmlToNdi.Chromium;

public class CefWrapper : IDisposable
{
    private bool disposedValue;
    private ChromiumWebBrowser? browser;
    private readonly int windowlessFrameRate;
    private Thread RenderWatchdog;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public string? Url { get; private set; }

    // Path-specific members
    private readonly bool useOutputBuffer;
    private readonly FrameRingBuffer? frameBuffer;
    private readonly FrameTimeAverager frameTimeAverager = new(120);
    private readonly object framePumpLock = new();
    private CancellationTokenSource? framePumpCancellation;
    private Task? framePumpTask;
    private readonly TimeSpan frameStallThreshold = TimeSpan.FromMilliseconds(1000);

    public CefWrapper(int width, int height, string initialUrl, bool useOutputBuffer, FrameRingBuffer? frameBuffer, int windowlessFrameRate)
    {
        this.Width = width;
        this.Height = height;
        this.Url = initialUrl;
        this.useOutputBuffer = useOutputBuffer;
        this.frameBuffer = frameBuffer;
        this.windowlessFrameRate = windowlessFrameRate;

        this.browser = new ChromiumWebBrowser(initialUrl)
        {
            AudioHandler = new CustomAudioHandler(),
        };

        this.browser.Size = new System.Drawing.Size(this.Width, this.Height);

        this.RenderWatchdog = new Thread(this.RenderWatchDogThread)
        {
            IsBackground = true,
            Name = "CefSharp Render Watchdog"
        };
    }

    private void RenderWatchDogThread()
    {
        while (!this.disposedValue)
        {
            if (!this.useOutputBuffer && this.frameTimeAverager.TimeSinceLastFrame >= this.frameStallThreshold)
            {
                this.RequestInvalidate();
            }
            Thread.Sleep(200);
        }
    }

    public async Task InitializeWrapperAsync()
    {
        if (this.browser is null)
        {
            return;
        }

        await this.browser.WaitForInitialLoadAsync();

        this.browser.GetBrowserHost().WindowlessFrameRate = this.windowlessFrameRate;
        this.browser.ToggleAudioMute();
        this.browser.Paint += this.OnBrowserPaint;

        if (!this.useOutputBuffer)
        {
            this.frameTimeAverager.Reset();
            this.StartFramePump();
        }

        this.RenderWatchdog.Start();
    }

    private void OnBrowserPaint(object? sender, OnPaintEventArgs e)
    {
        if (this.useOutputBuffer)
        {
            if (this.frameBuffer is null) return;
            try
            {
                var stride = e.Stride > 0 ? e.Stride : e.Width * 4;
                using var frame = VideoFrame.FromPointer(e.BufferHandle, e.Width, e.Height, stride, DateTime.UtcNow);
                this.frameBuffer.Enqueue(frame);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to capture Chromium frame for buffering {Width}x{Height}", e.Width, e.Height);
            }
        }
        else
        {
            if (Program.NdiSenderPtr == nint.Zero) return;

            var frameRate = this.frameTimeAverager.RegisterFrame();
            var rationalRate = frameRate.HasValue
                ? FrameRateRational.FromFramesPerSecond(frameRate.Value)
                : FrameRateRational.Default;

            var videoFrame = new NDIlib.video_frame_v2_t()
            {
                FourCC = NDIlib.FourCC_type_e.FourCC_type_BGRA,
                frame_rate_N = rationalRate.Numerator,
                frame_rate_D = rationalRate.Denominator,
                frame_format_type = NDIlib.frame_format_type_e.frame_format_type_progressive,
                line_stride_in_bytes = e.Width * 4,
                picture_aspect_ratio = (float)e.Width / e.Height,
                p_data = e.BufferHandle,
                timecode = NDIlib.send_timecode_synthesize,
                xres = e.Width,
                yres = e.Height,
            };
            NDIlib.send_send_video_v2(Program.NdiSenderPtr, ref videoFrame);
        }
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!this.disposedValue)
        {
            this.disposedValue = true;
            if (disposing)
            {
                if (!this.useOutputBuffer)
                {
                    this.StopFramePump();
                }

                if (this.RenderWatchdog.IsAlive)
                {
                    this.RenderWatchdog.Join(TimeSpan.FromSeconds(1));
                }

                if (this.browser is not null)
                {
                    this.browser.Paint -= this.OnBrowserPaint;
                    this.browser.Dispose();
                }
                this.browser = null;
            }
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
        this.browser.SendMouseWheelEvent(0, 0, 0, increment, CefEventFlags.None); 
    }

    public void Click(int x, int y)
    {
        var host = this.browser?.GetBrowser()?.GetHost();
        if(host is null)
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
        foreach(var c in model.ToSend)
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
        this.browser.Reload();
    }

    private void StartFramePump()
    {
        if (this.browser is null)
        {
            return;
        }

        var browserHost = this.browser.GetBrowserHost();
        var frameInterval = TimeSpan.FromSeconds(1.0 / 60); // Hardcoded to 60fps for the pump

        lock (this.framePumpLock)
        {
            this.framePumpCancellation?.Cancel();
            this.framePumpCancellation?.Dispose();

            var cancellation = new CancellationTokenSource();
            this.framePumpCancellation = cancellation;

            this.framePumpTask = Task.Run(async () =>
            {
                var stopwatch = Stopwatch.StartNew();
                var nextTick = stopwatch.Elapsed;

                while (!cancellation.IsCancellationRequested && !this.disposedValue)
                {
                    nextTick += frameInterval;
                    try
                    {
                        await Cef.UIThreadTaskFactory.StartNew(() =>
                        {
                            if (!cancellation.IsCancellationRequested && !this.disposedValue)
                            {
                                browserHost.Invalidate(PaintElementType.View);
                            }
                        }, cancellation.Token).ConfigureAwait(false);
                    }
                    catch (TaskCanceledException)
                    {
                        break;
                    }

                    var delay = nextTick - stopwatch.Elapsed;
                    if (delay > TimeSpan.Zero)
                    {
                        try
                        {
                            await Task.Delay(delay, cancellation.Token).ConfigureAwait(false);
                        }
                        catch (TaskCanceledException)
                        {
                            break;
                        }
                    }
                    else if (delay < -frameInterval)
                    {
                        nextTick = stopwatch.Elapsed;
                    }
                    else
                    {
                        await Task.Yield();
                    }
                }
            }, cancellation.Token);
        }
    }

    private void StopFramePump()
    {
        CancellationTokenSource? cancellation;
        Task? pumpTask;

        lock (this.framePumpLock)
        {
            cancellation = this.framePumpCancellation;
            pumpTask = this.framePumpTask;
            this.framePumpCancellation = null;
            this.framePumpTask = null;
        }

        if (cancellation is not null)
        {
            cancellation.Cancel();
        }

        if (pumpTask is not null)
        {
            try
            {
                pumpTask.Wait(TimeSpan.FromSeconds(1));
            }
            catch (AggregateException ex) when (ex.InnerException is TaskCanceledException)
            {
            }
            catch (TaskCanceledException)
            {
            }
        }

        cancellation?.Dispose();
    }

    private void RequestInvalidate()
    {
        var host = this.browser?.GetBrowserHost();
        if (host is null)
        {
            return;
        }
        _ = Cef.UIThreadTaskFactory.StartNew(() =>
        {
            if (!this.disposedValue)
            {
                host.Invalidate(PaintElementType.View);
            }
        });
    }
}