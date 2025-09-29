
using System.Buffers;
using System.Diagnostics;
using CefSharp;
using CefSharp.OffScreen;
using NewTek;

namespace Tractus.HtmlToNdi.Chromium;

public class CefWrapper : IDisposable
{
    private bool disposedValue;
    private ChromiumWebBrowser? browser;
    private readonly double targetFramesPerSecond;
    private readonly bool useBufferedPipeline;
    private readonly FrameRingBuffer? frameRingBuffer;
    private readonly FramePacer? framePacer;
    private readonly int frameRateNumerator;
    private readonly int frameRateDenominator;

    public int Width { get; private set; }
    public int Height { get; private set; }

    public string? Url { get; private set; }

    private Thread RenderWatchdog;
    private DateTime lastPaint = DateTime.MinValue;
    private CancellationTokenSource? framePumpCancellation;
    private Task? framePumpTask;

    public CefWrapper(int width, int height, string initialUrl, double targetFramesPerSecond, int bufferDepth)
    {
        this.Width = width;
        this.Height = height;
        this.Url = initialUrl;
        this.targetFramesPerSecond = targetFramesPerSecond;
        this.frameRateNumerator = (int)Math.Round(targetFramesPerSecond);
        this.frameRateDenominator = 1;
        this.useBufferedPipeline = bufferDepth > 0;

        this.browser = new ChromiumWebBrowser(initialUrl)
        {
            AudioHandler = new CustomAudioHandler(),
        };

        this.browser.Size = new System.Drawing.Size(this.Width, this.Height);

        this.RenderWatchdog = new Thread(this.RenderWatchDogThread);

        if (this.useBufferedPipeline)
        {
            this.frameRingBuffer = new FrameRingBuffer(bufferDepth);
            this.framePacer = new FramePacer(
                this.frameRingBuffer,
                targetFramesPerSecond,
                frame => this.TrySendBufferedFrame(frame));
        }
    }

    private void RenderWatchDogThread()
    {
        while (!this.disposedValue)
        {
            if(DateTime.Now.Subtract(this.lastPaint).TotalSeconds >= 1.0)
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

        this.browser.GetBrowserHost().WindowlessFrameRate = (int)Math.Round(this.targetFramesPerSecond);
        this.browser.ToggleAudioMute();

        this.browser.Paint += this.OnBrowserPaint;
        this.RenderWatchdog.Start();
        this.StartFramePump();
    }

    private void OnBrowserPaint(object? sender, OnPaintEventArgs e)
    {
        if (Program.NdiSenderPtr == nint.Zero)
        {
            return;
        }

        var browser = sender as ChromiumWebBrowser;

        if (browser is null)
        {
            return;
        }

        this.lastPaint = DateTime.Now;

        if (this.useBufferedPipeline && this.frameRingBuffer is not null)
        {
            var frame = VideoFrame.FromPaintBuffer(e.BufferHandle, e.Width, e.Height, e.Width * 4);
            this.frameRingBuffer.Enqueue(frame);
            return;
        }

        this.SendDirectFrame(e.BufferHandle, e.Width, e.Height, e.Width * 4);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!this.disposedValue)
        {
            if (disposing)
            {
                this.StopFramePump();

                this.framePacer?.Dispose();

                if (this.browser is not null)
                {
                    this.browser.Paint -= this.OnBrowserPaint;
                    this.browser.Dispose();
                }

                this.browser = null;
            }

            // TODO: free unmanaged resources (unmanaged objects) and override finalizer
            // TODO: set large fields to null
            this.disposedValue = true;
        }
    }

    public void Dispose()
    {
        // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
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
        this.framePumpCancellation = new CancellationTokenSource();
        var token = this.framePumpCancellation.Token;

        this.framePumpTask = Task.Run(async () =>
        {
            var interval = TimeSpan.FromSeconds(1d / this.targetFramesPerSecond);
            var stopwatch = Stopwatch.StartNew();
            long tick = 0;

            while (!token.IsCancellationRequested)
            {
                var nextTarget = interval * tick;
                var delay = nextTarget - stopwatch.Elapsed;

                if (delay > TimeSpan.Zero)
                {
                    try
                    {
                        await Task.Delay(delay, token).ConfigureAwait(false);
                    }
                    catch (TaskCanceledException)
                    {
                        break;
                    }
                }

                this.browser?.GetBrowserHost()?.Invalidate(PaintElementType.View);
                tick++;
            }
        }, token);
    }

    private void StopFramePump()
    {
        if (this.framePumpCancellation is null)
        {
            return;
        }

        this.framePumpCancellation.Cancel();

        try
        {
            this.framePumpTask?.Wait();
        }
        catch (AggregateException ex) when (ex.InnerException is TaskCanceledException)
        {
        }
        catch (TaskCanceledException)
        {
        }

        this.framePumpCancellation.Dispose();
        this.framePumpCancellation = null;
        this.framePumpTask = null;
    }

    private void SendDirectFrame(IntPtr bufferHandle, int width, int height, int stride)
    {
        if (Program.NdiSenderPtr == nint.Zero)
        {
            return;
        }

        var videoFrame = new NDIlib.video_frame_v2_t()
        {
            FourCC = NDIlib.FourCC_type_e.FourCC_type_BGRA,
            frame_rate_N = this.frameRateNumerator,
            frame_rate_D = this.frameRateDenominator,
            frame_format_type = NDIlib.frame_format_type_e.frame_format_type_progressive,
            line_stride_in_bytes = stride,
            picture_aspect_ratio = (float)width / height,
            p_data = bufferHandle,
            timecode = NDIlib.send_timecode_synthesize,
            xres = width,
            yres = height,
        };

        NDIlib.send_send_video_v2(Program.NdiSenderPtr, ref videoFrame);
    }

    private bool TrySendBufferedFrame(VideoFrame frame)
    {
        if (Program.NdiSenderPtr == nint.Zero)
        {
            return false;
        }

        using var handle = frame.Memory.Pin();
        var videoFrame = new NDIlib.video_frame_v2_t()
        {
            FourCC = NDIlib.FourCC_type_e.FourCC_type_BGRA,
            frame_rate_N = this.frameRateNumerator,
            frame_rate_D = this.frameRateDenominator,
            frame_format_type = NDIlib.frame_format_type_e.frame_format_type_progressive,
            line_stride_in_bytes = frame.Stride,
            picture_aspect_ratio = (float)frame.Width / frame.Height,
            p_data = (nint)handle.Pointer,
            timecode = NDIlib.send_timecode_synthesize,
            xres = frame.Width,
            yres = frame.Height,
        };

        NDIlib.send_send_video_v2(Program.NdiSenderPtr, ref videoFrame);
        return true;
    }
}
