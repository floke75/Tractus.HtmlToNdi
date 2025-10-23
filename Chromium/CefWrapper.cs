using CefSharp;
using CefSharp.OffScreen;
using Serilog;
using Tractus.HtmlToNdi.Video;

namespace Tractus.HtmlToNdi.Chromium;

/// <summary>
/// Manages the lifecycle and interaction with a CefSharp off-screen browser instance.
/// </summary>
internal class CefWrapper : IDisposable
{
    private bool disposedValue;
    private ChromiumWebBrowser? browser;

    /// <summary>
    /// Gets the width of the browser view.
    /// </summary>
    public int Width { get; private set; }

    /// <summary>
    /// Gets the height of the browser view.
    /// </summary>
    public int Height { get; private set; }

    /// <summary>
    /// Gets the current URL loaded in the browser.
    /// </summary>
    public string? Url { get; private set; }

    private readonly NdiVideoPipeline videoPipeline;
    private readonly FrameRate frameRate;
    private readonly int? windowlessFrameRateOverride;
    private FramePump? framePump;
    private readonly ILogger logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CefWrapper"/> class.
    /// </summary>
    /// <param name="width">The initial width of the browser.</param>
    /// <param name="height">The initial height of the browser.</param>
    /// <param name="initialUrl">The initial URL to load.</param>
    /// <param name="pipeline">The video pipeline to send frames to.</param>
    /// <param name="frameRate">The target frame rate.</param>
    /// <param name="logger">The logger instance.</param>
    /// <param name="windowlessFrameRateOverride">An optional override for the windowless frame rate.</param>
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

    /// <summary>
    /// Asynchronously initializes the browser wrapper, waiting for the initial page load
    /// and setting up paint handlers and the frame pump.
    /// </summary>
    /// <returns>A task that represents the asynchronous initialization operation.</returns>
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

    /// <summary>
    /// Releases the resources used by the <see cref="CefWrapper"/>.
    /// </summary>
    public void Dispose()
    {
        // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        this.Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Loads a new URL in the browser.
    /// </summary>
    /// <param name="url">The URL to load.</param>
    public void SetUrl(string? url)
    {
        if (this.browser is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(url))
        {
            this.logger.Warning("Ignoring request to load an empty URL");
            return;
        }

        this.Url = url;

        this.browser.Load(url);
    }

    /// <summary>
    /// Scrolls the browser view by a specified increment.
    /// </summary>
    /// <param name="increment">The amount to scroll.</param>
    public void ScrollBy(int increment)
    {
        this.browser.SendMouseWheelEvent(0, 0, 0, increment, CefEventFlags.None); 
    }

    /// <summary>
    /// Simulates a mouse click at the specified coordinates.
    /// </summary>
    /// <param name="x">The x-coordinate of the click.</param>
    /// <param name="y">The y-coordinate of the click.</param>
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

    /// <summary>
    /// Sends a series of keystrokes to the browser.
    /// </summary>
    /// <param name="model">The model containing the keystrokes to send.</param>
    public void SendKeystrokes(Models.SendKeystrokeModel? model)
    {
        if (model is null)
        {
            this.logger.Warning("Ignoring keystroke request because the payload was null");
            return;
        }

        if (string.IsNullOrEmpty(model.ToSend))
        {
            this.logger.Warning("Ignoring keystroke request because the payload was empty");
            return;
        }

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

    /// <summary>
    /// Refreshes the current page in the browser.
    /// </summary>
    public void RefreshPage()
    {
        this.browser.Reload();
    }
}
