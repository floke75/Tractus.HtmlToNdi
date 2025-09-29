using CefSharp;
using CefSharp.OffScreen;
using Serilog;
using Tractus.HtmlToNdi.Video;

namespace Tractus.HtmlToNdi.Chromium;

public class CefWrapper : IDisposable
{
    private bool disposedValue;
    private ChromiumWebBrowser? browser;
    private readonly FrameRingBuffer frameBuffer;
    private readonly int chromiumFrameRate;

    public int Width { get; private set; }
    public int Height { get; private set; }

    public string? Url { get; private set; }

    private Thread RenderWatchdog;
    private DateTime lastPaint = DateTime.MinValue;

    public CefWrapper(int width, int height, string initialUrl, FrameRingBuffer frameBuffer, int chromiumFrameRate)
    {
        this.Width = width;
        this.Height = height;
        this.Url = initialUrl;
        this.frameBuffer = frameBuffer ?? throw new ArgumentNullException(nameof(frameBuffer));
        this.chromiumFrameRate = chromiumFrameRate;

        this.browser = new ChromiumWebBrowser(initialUrl)
        {
            AudioHandler = new CustomAudioHandler(),
        };

        this.browser.Size = new System.Drawing.Size(this.Width, this.Height);

        this.RenderWatchdog = new Thread(this.RenderWatchDogThread);
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

        this.browser.GetBrowserHost().WindowlessFrameRate = this.chromiumFrameRate;
        this.browser.ToggleAudioMute();

        this.browser.Paint += this.OnBrowserPaint;
        this.RenderWatchdog.Start();
    }

    private void OnBrowserPaint(object? sender, OnPaintEventArgs e)
    {
        this.lastPaint = DateTime.Now;

        if (e.Width <= 0 || e.Height <= 0)
        {
            return;
        }

        var stride = e.Width * 4;
        var expectedLength = stride * e.Height;
        if (e.Buffer is null || e.Buffer.Length < expectedLength)
        {
            Log.Warning("Received paint buffer smaller than expected (length={Length}, expected={Expected}).", e.Buffer?.Length ?? 0, expectedLength);
            return;
        }

        var pixels = new ReadOnlySpan<byte>(e.Buffer, 0, expectedLength);
        this.frameBuffer.WriteFrame(pixels, e.Width, e.Height, stride, DateTime.UtcNow);
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
        this.browser?.Reload();
    }
}
