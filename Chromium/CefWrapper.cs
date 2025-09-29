using CefSharp;
using CefSharp.OffScreen;
using System;
using System.Threading;

namespace Tractus.HtmlToNdi.Chromium;

public class CefWrapper : IDisposable
{
    private bool disposedValue;
    private ChromiumWebBrowser? browser;
    private readonly NdiVideoPipeline videoPipeline;
    private readonly NdiVideoPipelineOptions pipelineOptions;
    private FramePump? framePump;

    public int Width { get; private set; }
    public int Height { get; private set; }

    public string? Url { get; private set; }

    private Thread RenderWatchdog;
    private DateTime lastPaint = DateTime.MinValue;

    public CefWrapper(int width, int height, string initialUrl, NdiVideoPipelineOptions pipelineOptions)
    {
        this.Width = width;
        this.Height = height;
        this.Url = initialUrl;
        this.pipelineOptions = pipelineOptions ?? throw new ArgumentNullException(nameof(pipelineOptions));

        this.browser = new ChromiumWebBrowser(initialUrl)
        {
            AudioHandler = new CustomAudioHandler(),
        };

        this.browser.Size = new System.Drawing.Size(this.Width, this.Height);

        this.RenderWatchdog = new Thread(this.RenderWatchDogThread);
        this.RenderWatchdog.IsBackground = true;
        this.videoPipeline = new NdiVideoPipeline(this.pipelineOptions, () => Program.NdiSenderPtr);
    }

    private void RenderWatchDogThread()
    {
        while (!this.disposedValue)
        {
            if(DateTime.Now.Subtract(this.lastPaint).TotalSeconds >= 1.0)
            {
                var host = this.browser?.GetBrowser()?.GetHost();
                host?.Invalidate(PaintElementType.View);
            }

            this.framePump?.EnsureWatchdog();

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

        this.browser.GetBrowserHost().WindowlessFrameRate = (int)Math.Max(1, Math.Round(this.pipelineOptions.TargetFrameRate));
        this.browser.ToggleAudioMute();

        this.browser.Paint += this.OnBrowserPaint;
        this.framePump = new FramePump(() => this.browser?.GetBrowser()?.GetHost(), this.pipelineOptions.TargetFrameRate);
        this.framePump.Start();
        this.RenderWatchdog.Start();
    }

    private void OnBrowserPaint(object? sender, OnPaintEventArgs e)
    {
        var browser = sender as ChromiumWebBrowser;
        if (browser is null)
        {
            return;
        }

        this.lastPaint = DateTime.Now;
        this.videoPipeline.ProcessPaint(e);
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
                    this.framePump?.Dispose();
                    this.videoPipeline.Dispose();
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