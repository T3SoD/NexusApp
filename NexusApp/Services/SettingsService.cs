using System.IO;
using System.Text.Json;
using NexusApp.Models;

namespace NexusApp.Services;

public class SettingsService
{
    private static readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "NexusApp", "settings.json");

    private static readonly JsonSerializerOptions _opts = new() { WriteIndented = true };

    /// <summary>
    /// One-time move of user data from the old version-tagged AppData folder
    /// (%AppData%\Nexus_v4, used through 5.0.0) to the version-neutral
    /// %AppData%\NexusApp. Copies the existing files (settings.json + nexus.db)
    /// so upgraders keep their theme, window layout, pinned resources, work
    /// orders and scan history. Best-effort and idempotent: it no-ops once the
    /// new folder exists, and leaves the old folder in place as a backup.
    /// Call this once at startup before any settings/data is read.
    /// </summary>
    public static void MigrateLegacyAppData()
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var current = Path.Combine(appData, "NexusApp");
            var legacy  = Path.Combine(appData, "Nexus_v4");
            if (Directory.Exists(current) || !Directory.Exists(legacy)) return;
            Directory.CreateDirectory(current);
            foreach (var src in Directory.GetFiles(legacy))
                File.Copy(src, Path.Combine(current, Path.GetFileName(src)), overwrite: false);
        }
        catch { /* a fresh folder is acceptable if the copy fails */ }
    }

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
