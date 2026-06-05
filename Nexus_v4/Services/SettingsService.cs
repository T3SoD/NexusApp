using System.IO;
using System.Text.Json;
using Nexus_v4.Models;

namespace Nexus_v4.Services;

public class SettingsService
{
    private static readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Nexus_v4", "settings.json");

    private static readonly JsonSerializerOptions _opts = new() { WriteIndented = true };

    public AppSettings Current { get; private set; }

    public SettingsService()
    {
        Current = Load();
    }

    private AppSettings Load()
    {
        AppSettings settings;
        try
        {
            if (File.Exists(_path))
            {
                var json = File.ReadAllText(_path);
                settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
            else
            {
                settings = new AppSettings();
                settings.SettingsSchemaVersion = 1;
                return settings;
            }
        }
        catch { return new AppSettings(); }

        // Schema migrations — bump SettingsSchemaVersion after each one
        var migrated = false;
        if (settings.SettingsSchemaVersion < 1)
        {
            settings.OverlayOpacity = 0.76;
            settings.SettingsSchemaVersion = 1;
            migrated = true;
        }
        if (settings.SettingsSchemaVersion < 2)
        {
            settings.OverlayOpacity = 0.7;
            settings.SettingsSchemaVersion = 2;
            migrated = true;
        }
        if (settings.SettingsSchemaVersion < 3)
        {
            settings.OverlayOpacity = 0.7;
            settings.SettingsSchemaVersion = 3;
            migrated = true;
        }
        if (settings.SettingsSchemaVersion < 4)
        {
            // An existing settings file means this isn't a first run — don't show
            // the welcome wizard to upgraders. Genuine fresh installs have no file
            // and keep FirstRunComplete = false.
            settings.FirstRunComplete = true;
            settings.SettingsSchemaVersion = 4;
            migrated = true;
        }
        if (migrated) Save(settings);

        return settings;
    }

    private void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(settings, _opts));
        }
        catch { }
    }

    public void Save() => Save(Current);
}
