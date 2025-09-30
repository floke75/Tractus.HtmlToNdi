using System;
using System.Threading;

using System.Threading.Tasks;



using CefSharp;
using CefSharp.OffScreen;
using NewTek;
using Tractus.HtmlToNdi.Video;

namespace Tractus.HtmlToNdi.Chromium;

public class CefWrapper : IDisposable
{
    private bool disposedValue;
    private ChromiumWebBrowser? browser;
    private INdiVideoSink? videoSink;
    private readonly FrameRate windowlessFrameRate;

    public int Width { get; private set; }
    public int Height { get; private set; }

    public string? Url { get; private set; }

    private Thread RenderWatchdog;
    private DateTime lastPaint = DateTime.MinValue;

    public CefWrapper(int width, int height, string initialUrl, FrameRate windowlessFrameRate)
    {
        this.Width = width;
        this.Height = height;
        this.Url = initialUrl;
        this.windowlessFrameRate = windowlessFrameRate;

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

        var host = this.browser.GetBrowserHost();
        var frameRate = Math.Clamp((int)Math.Round(this.windowlessFrameRate.ToDouble()), 1, 240);
        host.WindowlessFrameRate = frameRate;
        this.browser.ToggleAudioMute();

        this.browser.Paint += this.OnBrowserPaint;
        this.RenderWatchdog.Start();
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

        this.videoSink?.HandleFrame(e);
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

    public void SetVideoSink(INdiVideoSink sink)
    {
        this.videoSink = sink;
    }

    public ValueTask InvalidateAsync()
    {
        this.browser?.GetBrowserHost()?.Invalidate(PaintElementType.View);
        return ValueTask.CompletedTask;
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
