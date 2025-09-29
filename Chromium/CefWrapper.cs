
using CefSharp;
using CefSharp.OffScreen;
using NewTek;
using Tractus.HtmlToNdi.Video;

namespace Tractus.HtmlToNdi.Chromium;

public class CefWrapper : IDisposable
{
    private bool disposedValue;
    private ChromiumWebBrowser? browser;

    public int Width { get; private set; }
    public int Height { get; private set; }

    public string? Url { get; private set; }

    private DateTime lastPaint = DateTime.MinValue;
    private FramePump? framePump;
    private Action<OnPaintEventArgs, DateTime>? frameHandler;

    public CefWrapper(int width, int height, string initialUrl)
    {
        this.Width = width;
        this.Height = height;
        this.Url = initialUrl;

        this.browser = new ChromiumWebBrowser(initialUrl)
        {
            AudioHandler = new CustomAudioHandler(),
        };

        this.browser.Size = new System.Drawing.Size(this.Width, this.Height);
    }

    public async Task InitializeWrapperAsync()
    {
        if (this.browser is null)
        {
            return;
        }

        await this.browser.WaitForInitialLoadAsync();

        this.browser.GetBrowserHost().WindowlessFrameRate = 60;
        this.browser.ToggleAudioMute();

        this.browser.Paint += this.OnBrowserPaint;
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

        this.lastPaint = DateTime.UtcNow;
        this.framePump?.NotifyPaint(this.lastPaint);

        var handler = this.frameHandler;
        if (handler is not null)
        {
            handler(e, this.lastPaint);
            return;
        }

        var fallbackFrame = new NDIlib.video_frame_v2_t()
        {
            FourCC = NDIlib.FourCC_type_e.FourCC_type_BGRA,
            frame_rate_N = 60,
            frame_rate_D = 1,
            frame_format_type = NDIlib.frame_format_type_e.frame_format_type_progressive,
            line_stride_in_bytes = e.Width * 4,
            picture_aspect_ratio = (float)e.Width / e.Height,
            p_data = e.BufferHandle,
            timecode = NDIlib.send_timecode_synthesize,
            xres = e.Width,
            yres = e.Height,
        };

        NDIlib.send_send_video_v2(Program.NdiSenderPtr, ref fallbackFrame);
    }

    internal ChromiumWebBrowser Browser => this.browser ?? throw new InvalidOperationException("Browser is not initialized.");

    public void AttachFramePump(FramePump pump)
    {
        this.framePump = pump;
    }

    public void SetFrameHandler(Action<OnPaintEventArgs, DateTime> handler)
    {
        this.frameHandler = handler;
    }

    public ValueTask InvalidateAsync()
    {
        if (this.browser is null)
        {
            return ValueTask.CompletedTask;
        }

        var host = this.browser.GetBrowserHost();
        if (host is null)
        {
            return ValueTask.CompletedTask;
        }

        if (Cef.CurrentlyOnThread(CefThreadIds.TID_UI))
        {
            host.Invalidate(PaintElementType.View);
            return ValueTask.CompletedTask;
        }

        return new ValueTask(Cef.UIThreadTaskFactory.StartNew(() => host.Invalidate(PaintElementType.View)));
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!this.disposedValue)
        {
            if (disposing)
            {
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
}
