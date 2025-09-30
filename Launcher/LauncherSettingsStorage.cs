using System;
using System.IO;
using System.Text.Json;
using Serilog;

namespace Tractus.HtmlToNdi.Launcher;

public static class LauncherSettingsStorage
{
    private const string FileName = "launcher-settings.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    public static LauncherSettings Load()
    {
        try
        {
            var path = Path.Combine(AppManagement.DataDirectory, FileName);
            if (!File.Exists(path))
            {
                return new LauncherSettings();
            }

            var json = File.ReadAllText(path);
            var settings = JsonSerializer.Deserialize<LauncherSettings>(json, SerializerOptions);
            return settings ?? new LauncherSettings();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load launcher settings. Reverting to defaults.");
            return new LauncherSettings();
        }
    }

    public static void Save(LauncherSettings settings)
    {
        try
        {
            var json = JsonSerializer.Serialize(settings, SerializerOptions);
            var path = Path.Combine(AppManagement.DataDirectory, FileName);
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to save launcher settings to disk.");
        }
    }
}
