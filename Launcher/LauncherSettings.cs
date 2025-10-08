namespace Tractus.HtmlToNdi.Launcher;

public class LauncherSettings
{
    public string NdiName { get; set; } = "HTML5";

    public int Port { get; set; } = 9999;

    public string Url { get; set; } = "https://testpattern.tractusevents.com/";

    public int Width { get; set; } = 1920;

    public int Height { get; set; } = 1080;

    public string FrameRate { get; set; } = "60";

    public bool EnableBuffering { get; set; }
        = false;

    public int BufferDepth { get; set; } = 3;

    public double TelemetryIntervalSeconds { get; set; } = 10;

    public string? WindowlessFrameRateOverride { get; set; }
        = null;

    public bool DisableGpuVsync { get; set; }
        = false;

    public bool DisableFrameRateLimit { get; set; }
        = false;

    public bool AllowLatencyExpansion { get; set; }
        = false;
}
