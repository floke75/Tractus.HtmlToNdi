using System.Collections.Generic;
using System.Globalization;

namespace Tractus.HtmlToNdi.Launcher;

public class LauncherSettings
{
    public string NdiName { get; set; } = "HTML5";

    public int Port { get; set; } = 9999;

    public string Url { get; set; } = "https://testpattern.tractusevents.com/";

    public int Width { get; set; } = 1920;

    public int Height { get; set; } = 1080;

    public string FrameRate { get; set; } = "60";

    public bool EnableOutputBuffer { get; set; }
        = false;

    public int BufferDepth { get; set; } = 3;

    public double TelemetryIntervalSeconds { get; set; } = 10;

    public string? WindowlessFrameRate { get; set; }
        = null;

    public bool DisableGpuVsync { get; set; }
        = false;

    public bool DisableFrameRateLimit { get; set; }
        = false;

    public IEnumerable<string> BuildArguments()
    {
        yield return $"--ndiname={NdiName}";
        yield return $"--port={Port}";
        yield return $"--url={Url}";
        yield return $"--w={Width}";
        yield return $"--h={Height}";

        if (!string.IsNullOrWhiteSpace(FrameRate))
        {
            yield return $"--fps={FrameRate.Trim()}";
        }

        if (EnableOutputBuffer)
        {
            yield return "--enable-output-buffer";

            if (BufferDepth > 0)
            {
                yield return $"--buffer-depth={BufferDepth}";
            }
        }

        if (TelemetryIntervalSeconds > 0)
        {
            yield return $"--telemetry-interval={TelemetryIntervalSeconds.ToString(CultureInfo.InvariantCulture)}";
        }

        if (!string.IsNullOrWhiteSpace(WindowlessFrameRate))
        {
            yield return $"--windowless-frame-rate={WindowlessFrameRate.Trim()}";
        }

        if (DisableGpuVsync)
        {
            yield return "--disable-gpu-vsync";
        }

        if (DisableFrameRateLimit)
        {
            yield return "--disable-frame-rate-limit";
        }
    }
}
