using System;
using System.IO;
using System.Text.Json;

namespace Tractus.HtmlToNdi.Launcher;

internal sealed class LauncherSettings
{
    public string? ApplicationPath { get; set; }
    public string NdiName { get; set; } = "HTML5";
    public int HttpPort { get; set; } = 9999;
    public string StartUrl { get; set; } = "https://testpattern.tractusevents.com/";
    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;
    public string? FrameRate { get; set; } = "60";
    public bool EnableBuffering { get; set; }
    public int BufferDepth { get; set; } = 3;
    public int TelemetryIntervalSeconds { get; set; } = 10;
    public int? WindowlessFrameRate { get; set; }
    public bool DisableGpuVsync { get; set; }
    public bool DisableFrameRateLimit { get; set; }
    public bool EnableDebugLogging { get; set; }
    public bool QuietConsoleLogging { get; set; }

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.General)
    {
        WriteIndented = true
    };

    private static string SettingsPath
    {
        get
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Tractus.HtmlToNdi");
            Directory.CreateDirectory(directory);
            return Path.Combine(directory, "launcher-settings.json");
        }
    }

    public static LauncherSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var settings = JsonSerializer.Deserialize<LauncherSettings>(json, SerializerOptions);
                if (settings != null)
                {
                    return settings;
                }
            }
        }
        catch
        {
            // Ignore malformed files and fall back to defaults.
        }

        return new LauncherSettings();
    }

    public static void Save(LauncherSettings settings)
    {
        var json = JsonSerializer.Serialize(settings, SerializerOptions);
        File.WriteAllText(SettingsPath, json);
    }
}
