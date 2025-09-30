using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tractus.HtmlToNdi.Launcher;

public class LauncherSettings
{
    private const string SettingsFileName = "launcher-settings.json";

    public string NdiName { get; set; } = "HTML5";

    public int Port { get; set; } = 9999;

    public string Url { get; set; } = "https://testpattern.tractusevents.com/";

    public int Width { get; set; } = 1920;

    public int Height { get; set; } = 1080;

    public string Fps { get; set; } = "60";

    public int? BufferDepth { get; set; }

    public bool EnableOutputBuffer { get; set; }
    public double TelemetryIntervalSeconds { get; set; } = 10;

    public double? WindowlessFrameRate { get; set; }

    public bool DisableGpuVsync { get; set; }

    public bool DisableFrameRateLimit { get; set; }

    public bool DebugLogging { get; set; }

    public bool QuietLogging { get; set; }
    public static LauncherSettings Load()
    {
        try
        {
            var path = Path.Combine(AppManagement.DataDirectory, SettingsFileName);
            if (!File.Exists(path))
            {
                return new LauncherSettings();
            }

            var json = File.ReadAllText(path);
            var settings = JsonSerializer.Deserialize<LauncherSettings>(json, GetSerializerOptions());
            return settings ?? new LauncherSettings();
        }
        catch
        {
            return new LauncherSettings();
        }
    }

    public void Save()
    {
        var path = Path.Combine(AppManagement.DataDirectory, SettingsFileName);
        var json = JsonSerializer.Serialize(this, GetSerializerOptions());
        File.WriteAllText(path, json);
    }

    public string[] ToArgs()
    {
        var args = new List<string>
        {
            $"--ndiname={NdiName}",
            $"--port={Port}",
            $"--url={Url}",
            $"--w={Width}",
            $"--h={Height}",
        };

        if (!string.IsNullOrWhiteSpace(Fps))
        {
            args.Add($"--fps={Fps}");
        }

        if (BufferDepth.HasValue && BufferDepth.Value > 0)
        {
            args.Add($"--buffer-depth={BufferDepth.Value}");
        }
        else if (EnableOutputBuffer)
        {
            args.Add("--enable-output-buffer");
        }

        if (TelemetryIntervalSeconds > 0)
        {
            args.Add($"--telemetry-interval={TelemetryIntervalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        }

        if (WindowlessFrameRate.HasValue && WindowlessFrameRate.Value > 0)
        {
            args.Add($"--windowless-frame-rate={WindowlessFrameRate.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        }

        if (DisableGpuVsync)
        {
            args.Add("--disable-gpu-vsync");
        }

        if (DisableFrameRateLimit)
        {
            args.Add("--disable-frame-rate-limit");
        }

        if (DebugLogging)
        {
            args.Add("-debug");
        }

        if (QuietLogging)
        {
            args.Add("-quiet");
        }

        return args.ToArray();
    }

    private static JsonSerializerOptions GetSerializerOptions() => new()
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
