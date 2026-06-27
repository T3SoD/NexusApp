using System.IO;
using System.Linq;
using System.Text.Json;
using NexusApp.Models;

namespace NexusApp.Services;

public class SettingsService
{
    private readonly string _path;

    private static string DefaultPath => Path.Combine(
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

    /// <param name="settingsPath">
    /// Override the settings file location. Defaults to %AppData%\NexusApp\settings.json.
    /// Used by tests to point at a temp file instead of the real user profile.
    /// </param>
    public SettingsService(string? settingsPath = null)
    {
        _path = settingsPath ?? DefaultPath;
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
        catch (Exception ex) { Logger.Error($"Failed to load settings from {_path}; using defaults", ex); return new AppSettings(); }

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
        if (settings.SettingsSchemaVersion < 5)
        {
            // PerMonitorV2 (issue #6): a ScanRegion saved under the old System-DPI-aware process
            // encodes VIRTUALIZED coords. Negative X/Y means it was drawn on a left/up secondary
            // monitor and no longer maps to the right physical pixels under PMv2, so drop it and let
            // the user redraw once (the magenta full-screen fallback covers the gap). Primary-monitor
            // regions (X>=0 && Y>=0) were already true-physical, so they are kept untouched.
            if (settings.ScanRegion is { } sr && (sr.X < 0 || sr.Y < 0))
                settings.ScanRegion = null;
            settings.SettingsSchemaVersion = 5;
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
        catch (Exception ex) { Logger.Error($"Failed to save settings to {_path}", ex); }
    }

    public void Save() => Save(Current);

    // ── Blueprint ownership ────────────────────────────────────────────────────
    // Stored name-keyed in settings.json (not nexus.db) so ownership survives the
    // database reseed that happens on every app update.

    // O(1) membership lookups. The List<string> is what gets serialized; this set
    // mirrors it so the per-row ownership checks during a nav render are not O(n²).
    private HashSet<string>? _ownedSet;
    private HashSet<string> OwnedSet =>
        _ownedSet ??= new HashSet<string>(Current.OwnedBlueprints, StringComparer.OrdinalIgnoreCase);

    public int OwnedBlueprintCount => Current.OwnedBlueprints.Count;

    public bool IsBlueprintOwned(string name) => OwnedSet.Contains(name);

    public void SetBlueprintOwned(string name, bool owned)
    {
        if (owned)
        {
            if (!OwnedSet.Add(name)) return;      // already owned — no change, no disk write
            Current.OwnedBlueprints.Add(name);
        }
        else
        {
            if (!OwnedSet.Remove(name)) return;   // wasn't owned — no change, no disk write
            Current.OwnedBlueprints.RemoveAll(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
        }
        Save();
    }

    // Bulk-mark owned with a single disk write — used by the Game.log importer so a
    // retroactive scan of dozens of blueprints doesn't save settings dozens of times.
    // Returns how many were newly marked (already-owned ones are skipped).
    public int SetBlueprintsOwned(IEnumerable<string> names)
    {
        int added = 0;
        foreach (var name in names)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (OwnedSet.Add(name)) { Current.OwnedBlueprints.Add(name); added++; }
        }
        if (added > 0) Save();
        return added;
    }

    public void ClearOwnedBlueprints()
    {
        Current.OwnedBlueprints.Clear();
        _ownedSet = null;   // force the lookup set to rebuild from the now-empty list
        Save();
    }

    public void ClearPinnedResources()
    {
        Current.PinnedResources.Clear();
        Save();
    }

    // ── Blueprint Network local identity ───────────────────────────────────────
    // Only the local user's own identity lives in settings; the shared roster is in network.db.

    /// <summary>Returns the stable per-user GUID that identifies this user across exported
    /// library files, generating and persisting it on first use.</summary>
    public string EnsureLocalNetworkId()
    {
        if (string.IsNullOrEmpty(Current.LocalNetworkId))
        {
            Current.LocalNetworkId = Guid.NewGuid().ToString();
            Save();
        }
        return Current.LocalNetworkId;
    }

    /// <summary>Records how the user wants to be shown when they export — their RSI handle or a
    /// freeform nickname. The GUID, not this label, is the stable identity.</summary>
    public void SetLocalIdentity(string displayName, string identityKind)
    {
        Current.LocalDisplayName = displayName ?? "";
        Current.LocalIdentityKind =
            identityKind == NetworkIdentityKind.Nickname ? NetworkIdentityKind.Nickname : NetworkIdentityKind.Handle;
        Save();
    }

    /// <summary>Caches the RSI handle detected from Game.log (used to pre-fill the export label).
    /// No-ops when unchanged so it doesn't rewrite settings on every matching login line.</summary>
    public void SetDetectedRsiHandle(string handle)
    {
        if (string.IsNullOrWhiteSpace(handle) || handle == Current.DetectedRsiHandle) return;
        Current.DetectedRsiHandle = handle;
        Save();
    }

    /// <summary>Reset the local Blueprint Network identity (GUID + label + detected RSI handle) — used
    /// by "Clear saved data". A fresh GUID is generated on the next launch.</summary>
    public void ClearLocalNetworkIdentity()
    {
        Current.LocalNetworkId = "";
        Current.LocalDisplayName = "";
        Current.LocalIdentityKind = NetworkIdentityKind.Handle;
        Current.DetectedRsiHandle = "";
        Save();
    }
}
