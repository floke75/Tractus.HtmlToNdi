
using CefSharp;
using CefSharp.OffScreen;
using Serilog;
using Tractus.HtmlToNdi.Video;

namespace Tractus.HtmlToNdi.Chromium;

public class CefWrapper : IDisposable
{
    private bool disposedValue;
    private ChromiumWebBrowser? browser;

    public int Width { get; private set; }
    public int Height { get; private set; }

    public string? Url { get; private set; }

    private readonly NdiVideoPipeline videoPipeline;
    private readonly FrameRate frameRate;
    private readonly int? windowlessFrameRateOverride;
    private FramePump? framePump;
    private readonly ILogger logger;

    public CefWrapper(int width, int height, string initialUrl, NdiVideoPipeline pipeline, FrameRate frameRate, ILogger logger, int? windowlessFrameRateOverride)
    {
        this.Width = width;
        this.Height = height;
        this.Url = initialUrl;
        this.videoPipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        this.frameRate = frameRate;
        this.logger = logger;
        this.windowlessFrameRateOverride = windowlessFrameRateOverride;

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

        var targetWindowlessRate = this.windowlessFrameRateOverride ?? Math.Clamp((int)Math.Round(this.frameRate.Value), 1, 240);
        this.browser.GetBrowserHost().WindowlessFrameRate = targetWindowlessRate;
        this.browser.ToggleAudioMute();

        this.browser.Paint += this.OnBrowserPaint;

        this.framePump = new FramePump(this.browser, this.frameRate.FrameDuration, TimeSpan.FromSeconds(1), this.logger);
        this.framePump.Start();
        this.videoPipeline.Start();
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

        this.framePump?.NotifyPaint();

        var capturedFrame = new CapturedFrame(e.BufferHandle, e.Width, e.Height, e.Width * 4);
        this.videoPipeline.HandleFrame(capturedFrame);
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
                this.framePump?.Dispose();
                this.videoPipeline.Dispose();
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
