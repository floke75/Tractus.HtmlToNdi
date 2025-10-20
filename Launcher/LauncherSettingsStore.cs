using System;
using System.Text.Json;
using Serilog;

namespace Tractus.HtmlToNdi.Launcher;

/// <summary>
/// Manages loading and saving of launcher settings.
/// </summary>
public static class LauncherSettingsStore
{
    private const string SettingsFileName = "launcher-settings.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>
    /// Loads the launcher settings from the settings file.
    /// </summary>
    /// <returns>The loaded launcher settings, or default settings if loading fails.</returns>
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
            var settings = JsonSerializer.Deserialize<LauncherSettings>(json, SerializerOptions);
            return settings ?? new LauncherSettings();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load launcher settings. Using defaults.");
            return new LauncherSettings();
        }
    }

    /// <summary>
    /// Saves the launcher settings to the settings file.
    /// </summary>
    /// <param name="settings">The settings to save.</param>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="settings"/> is null.</exception>
    public static void Save(LauncherSettings settings)
    {
        if (settings is null)
        {
            throw new ArgumentNullException(nameof(settings));
        }

        try
        {
            var json = JsonSerializer.Serialize(settings, SerializerOptions);
            var path = Path.Combine(AppManagement.DataDirectory, SettingsFileName);
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to save launcher settings.");
        }
    }
}
