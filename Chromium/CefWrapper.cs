using CefSharp;
using CefSharp.OffScreen;
using Serilog;
using System.Diagnostics;
using Tractus.HtmlToNdi.Native;
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
    private readonly bool compositorCaptureRequested;
    private CompositorCaptureBridge? compositorCaptureBridge;

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
        this.compositorCaptureRequested = pipeline.Options.EnableCompositorCapture;

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

        var host = this.browser.GetBrowserHost();
        var targetWindowlessRate = this.windowlessFrameRateOverride ?? Math.Clamp((int)Math.Round(this.frameRate.Value), 1, 240);
        host.WindowlessFrameRate = targetWindowlessRate;
        this.browser.ToggleAudioMute();

        if (this.compositorCaptureRequested && this.TryActivateCompositorCapture(host))
        {
            this.videoPipeline.AttachInvalidationScheduler(null);
            this.videoPipeline.Start();
            return;
        }

        this.browser.Paint += this.OnBrowserPaint;

        var pipelineOptions = this.videoPipeline.Options;
        var pumpMode = pipelineOptions.EnablePacedInvalidation
            ? FramePumpMode.OnDemand
            : FramePumpMode.Periodic;

        this.framePump = new FramePump(
            this.browser,
            this.frameRate.FrameDuration,
            TimeSpan.FromSeconds(1),
            this.logger,
            pumpMode,
            pipelineOptions.EnablePumpCadenceAdaptation);
        this.framePump.Start();
        this.videoPipeline.AttachInvalidationScheduler(this.framePump);
        this.videoPipeline.Start();
    }

    /// <summary>
    /// Handles Chromium paint callbacks and forwards frames into the legacy invalidation-driven pipeline.
    /// </summary>
    /// <param name="sender">The browser raising the paint event.</param>
    /// <param name="e">The paint event arguments.</param>
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

        var capturedFrame = new CapturedFrame(
            e.BufferHandle,
            e.Width,
            e.Height,
            e.Width * 4,
            Stopwatch.GetTimestamp(),
            DateTime.UtcNow);
        this.videoPipeline.HandleFrame(capturedFrame);
    }

    /// <summary>
    /// Attempts to enable compositor-driven capture by disabling Chromium's auto begin frames and starting the native bridge.
    /// </summary>
    /// <param name="host">The browser host that exposes compositor control.</param>
    /// <returns><c>true</c> when compositor capture is active; otherwise <c>false</c>.</returns>
    private bool TryActivateCompositorCapture(IBrowserHost host)
    {
        this.logger.Information("Attempting compositor-driven capture");

        try
        {
            host.SetAutoBeginFrameEnabled(false);
        }
        catch (NotSupportedException ex)
        {
            this.logger.Warning(ex, "Browser host does not support manual begin-frame control");
            return false;
        }
        catch (Exception ex)
        {
            this.logger.Warning(ex, "Failed to disable Chromium auto begin frames");
            return false;
        }

        var bridge = new CompositorCaptureBridge(this.logger);
        bridge.FrameArrived += this.OnCompositorFrame;

        if (!bridge.TryStart(host, this.Width, this.Height, this.frameRate, out var error))
        {
            bridge.FrameArrived -= this.OnCompositorFrame;
            bridge.Dispose();
            this.TryRestoreAutoBeginFrame(host);

            if (!string.IsNullOrWhiteSpace(error))
            {
                this.logger.Warning("Compositor capture unavailable: {Error}", error);
            }

            return false;
        }

        this.logger.Information("Compositor capture enabled; FramePump disabled");
        this.compositorCaptureBridge = bridge;
        return true;
    }

    /// <summary>
    /// Handles compositor-delivered frames, forwarding them to the pipeline and ensuring native resources are released on error.
    /// </summary>
    /// <param name="sender">The compositor capture bridge that surfaced the frame.</param>
    /// <param name="frame">The captured frame payload.</param>
    private void OnCompositorFrame(object? sender, CapturedFrame frame)
    {
        if (Program.NdiSenderPtr == nint.Zero)
        {
            frame.Dispose();
            return;
        }

        try
        {
            this.videoPipeline.HandleCompositorFrame(frame);
        }
        catch (Exception ex)
        {
            this.logger.Warning(ex, "Unhandled exception while forwarding compositor frame");
            frame.Dispose();
        }
    }

    /// <summary>
    /// Re-enables Chromium's auto begin frame behaviour when the compositor experiment is disabled or disposed.
    /// </summary>
    /// <param name="host">The host to restore state on.</param>
    private void TryRestoreAutoBeginFrame(IBrowserHost host)
    {
        try
        {
            host.SetAutoBeginFrameEnabled(true);
        }
        catch (Exception ex)
        {
            this.logger.Debug(ex, "Failed to restore Chromium auto begin frame state");
        }
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!this.disposedValue)
        {
            if (disposing)
            {
                IBrowserHost? host = null;

                if (this.browser is not null)
                {
                    try
                    {
                        host = this.browser.GetBrowserHost();
                    }
                    catch (Exception ex)
                    {
                        this.logger.Debug(ex, "Failed to retrieve browser host during dispose");
                    }
                }

                if (this.compositorCaptureBridge is not null)
                {
                    this.compositorCaptureBridge.FrameArrived -= this.OnCompositorFrame;
                    this.compositorCaptureBridge.Dispose();
                    this.compositorCaptureBridge = null;

                    if (host is not null)
                    {
                        this.TryRestoreAutoBeginFrame(host);
                    }
                }

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
