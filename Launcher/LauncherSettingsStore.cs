using System.Text.Json;
using Serilog;

namespace Tractus.HtmlToNdi.Launcher;

public static class LauncherSettingsStore
{
    private const string SettingsFileName = "launcher-settings.json";

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
            var settings = JsonSerializer.Deserialize<LauncherSettings>(json);
            return settings ?? new LauncherSettings();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load launcher settings. Using defaults.");
            return new LauncherSettings();
        }
    }

    public static void Save(LauncherSettings settings)
    {
        if (settings is null)
        {
            throw new ArgumentNullException(nameof(settings));
        }

        try
        {
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            var path = Path.Combine(AppManagement.DataDirectory, SettingsFileName);
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to save launcher settings.");
        }
    }
}
