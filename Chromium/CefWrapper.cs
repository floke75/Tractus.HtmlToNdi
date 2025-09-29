using CefSharp;
using CefSharp.OffScreen;
using NewTek;

namespace Tractus.HtmlToNdi.Chromium;

public class CefWrapper : IDisposable
{
    private readonly double targetFrameRate;
    private readonly bool useBufferedPipeline;
    private readonly int bufferDepth;

    private bool disposedValue;
    private ChromiumWebBrowser? browser;

    public int Width { get; private set; }
    public int Height { get; private set; }

    public string? Url { get; private set; }

    private Thread RenderWatchdog;
    private DateTime lastPaint = DateTime.MinValue;

    private FramePump? framePump;
    private FrameRingBuffer? frameRingBuffer;
    private VideoFramePool? framePool;
    private FramePacer? framePacer;

    public CefWrapper(int width, int height, string initialUrl, double targetFrameRate, bool enableBufferedPipeline, int bufferDepth)
    {
        this.Width = width;
        this.Height = height;
        this.Url = initialUrl;
        this.targetFrameRate = targetFrameRate;
        this.useBufferedPipeline = enableBufferedPipeline;
        this.bufferDepth = Math.Max(2, bufferDepth);

        this.browser = new ChromiumWebBrowser(initialUrl)
        {
            AudioHandler = new CustomAudioHandler(),
        };

        this.browser.Size = new System.Drawing.Size(this.Width, this.Height);

        this.RenderWatchdog = new Thread(this.RenderWatchDogThread)
        {
            IsBackground = true,
            Name = "ChromiumRenderWatchdog"
        };

        if (this.useBufferedPipeline)
        {
            this.framePool = new VideoFramePool();
            this.frameRingBuffer = new FrameRingBuffer(this.bufferDepth);
        }
    }

    private void RenderWatchDogThread()
    {
        while (!this.disposedValue)
        {
            if (DateTime.Now.Subtract(this.lastPaint).TotalSeconds >= 1.0 && this.browser is not null)
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

        this.browser.GetBrowserHost().WindowlessFrameRate = (int)Math.Round(this.targetFrameRate);
        this.browser.ToggleAudioMute();

        this.browser.Paint += this.OnBrowserPaint;
        this.RenderWatchdog.Start();

        this.framePump = new FramePump(this.browser, this.targetFrameRate);

        if (this.useBufferedPipeline && this.frameRingBuffer is not null)
        {
            this.framePacer = new FramePacer(
                this.frameRingBuffer,
                this.targetFrameRate,
                frame => this.SendFrame(frame));
        }
    }

    private void OnBrowserPaint(object? sender, OnPaintEventArgs e)
    {
        if (this.browser is null)
        {
            return;
        }

        this.lastPaint = DateTime.Now;

        if (this.useBufferedPipeline)
        {
            if (this.framePool is null || this.frameRingBuffer is null)
            {
                return;
            }

            var frame = this.framePool.Rent(e.Width, e.Height, e.Stride);

            try
            {
                frame.CopyFrom(e.BufferHandle, e.Stride, e.Width, e.Height);
            }
            catch
            {
                frame.Release();
                throw;
            }

            var droppedFrame = this.frameRingBuffer.Enqueue(frame);
            droppedFrame?.Release();
            return;
        }

        if (Program.NdiSenderPtr == nint.Zero)
        {
            return;
        }

        this.SendFrame(e.BufferHandle, e.Width, e.Height, e.Stride);
    }

    private void SendFrame(VideoFrame frame)
    {
        if (Program.NdiSenderPtr == nint.Zero)
        {
            return;
        }

        var videoFrame = new NDIlib.video_frame_v2_t()
        {
            FourCC = NDIlib.FourCC_type_e.FourCC_type_BGRA,
            frame_rate_N = (int)Math.Round(this.targetFrameRate),
            frame_rate_D = 1,
            frame_format_type = NDIlib.frame_format_type_e.frame_format_type_progressive,
            line_stride_in_bytes = frame.Stride,
            picture_aspect_ratio = (float)frame.Width / frame.Height,
            p_data = frame.Pointer,
            timecode = NDIlib.send_timecode_synthesize,
            xres = frame.Width,
            yres = frame.Height,
        };

        NDIlib.send_send_video_v2(Program.NdiSenderPtr, ref videoFrame);
    }

    private void SendFrame(nint bufferHandle, int width, int height, int stride)
    {
        var videoFrame = new NDIlib.video_frame_v2_t()
        {
            FourCC = NDIlib.FourCC_type_e.FourCC_type_BGRA,
            frame_rate_N = (int)Math.Round(this.targetFrameRate),
            frame_rate_D = 1,
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

    protected virtual void Dispose(bool disposing)
    {
        if (!this.disposedValue)
        {
            if (disposing)
            {
                this.framePump?.Dispose();
                this.framePump = null;

                this.framePacer?.Dispose();
                this.framePacer = null;

                if (this.frameRingBuffer is not null)
                {
                    foreach (var frame in this.frameRingBuffer.Drain())
                    {
                        frame.Release();
                    }
                }

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
}
