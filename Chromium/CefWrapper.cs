
using CefSharp;
using CefSharp.OffScreen;
using NewTek;
using Tractus.HtmlToNdi.Video;

namespace Tractus.HtmlToNdi.Chromium;

public class CefWrapper : IDisposable
{
    private bool disposedValue;
    private ChromiumWebBrowser? browser;
    private readonly NdiVideoPipeline pipeline;
    private readonly FramePump framePump;
    private readonly FrameRate windowlessFrameRate;
    private readonly FrameRate outputFrameRate;

    public int Width { get; private set; }
    public int Height { get; private set; }

    public string? Url { get; private set; }

    private Thread RenderWatchdog;

    public CefWrapper(int width, int height, string initialUrl, NdiVideoPipeline pipeline, FramePump framePump, FrameRate windowlessFrameRate, FrameRate outputFrameRate)
    {
        this.Width = width;
        this.Height = height;
        this.Url = initialUrl;
        this.pipeline = pipeline;
        this.framePump = framePump;
        this.windowlessFrameRate = windowlessFrameRate;
        this.outputFrameRate = outputFrameRate;

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
            this.framePump.WatchdogTick();
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

        this.browser.GetBrowserHost().WindowlessFrameRate = (int)Math.Round(this.windowlessFrameRate.ToDouble());
        this.browser.ToggleAudioMute();

        this.browser.Paint += this.OnBrowserPaint;
        this.framePump.Attach(this.browser);
        this.framePump.Start();
        this.pipeline.Start(this.outputFrameRate);
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

        this.pipeline.HandlePaint(e);
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
        this.framePump.Dispose();
        this.pipeline.Dispose();
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
