using System.IO;
using System.Linq;
using System.Text.Json;
using NexusApp.Models;

namespace NexusApp.Services;

public class SettingsService
{
    private readonly string _path;

    private static string DefaultPath => Path.Combine(AppPaths.Root, "settings.json");

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
        if (!File.Exists(_path))
        {
            var fresh = new AppSettings();
            fresh.SettingsSchemaVersion = 1;
            return fresh;
        }

        var settings = TryReadSettings(_path);
        var recovered = false;
        if (settings == null)
        {
            // No path in the message: settings.json lives under %AppData%, whose full path carries
            // the Windows username into nexus.log (the market/SCT snapshot files' own rule). The
            // constant file name tells the whole story.
            Logger.Error("[WIN] settings.json failed to load; quarantining and attempting backup recovery");
            settings = RecoverFromCorrupt();
            recovered = true;
        }

        // Schema migrations - bump SettingsSchemaVersion after each one
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
            // An existing settings file means this isn't a first run - don't show
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
        // Recovery must re-persist unconditionally, NOT only when `migrated` fires: RecoverFromCorrupt
        // already removed the primary file from disk (quarantined it), and a .bak at the current
        // schema version (the realistic case for any already-migrated user) trips none of the
        // `< N` checks above, so `migrated` alone would leave settings.json missing on disk with
        // only the in-memory Current holding the recovered values - lost the moment the process
        // exits without a clean Closing-handler save (e.g. CrashGuard's Environment.Exit paths).
        if (migrated || recovered) Save(settings);

        return settings;
    }

    // Deserialize AppSettings from <path>. Returns null (never throws) on a missing/corrupt file
    // so Load can tell "no file" apart from "unreadable file" and only quarantine+recover the latter.
    private static AppSettings? TryReadSettings(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path));
        }
        catch (Exception ex) { Logger.Error($"Failed to read {Path.GetFileName(path)}", ex); return null; }
    }

    // Corrupt-file recovery ladder for settings.json (mirrors Services/Cargo/JsonFile's
    // quarantine-and-.bak pattern): set the unreadable primary aside for diagnosis, try the
    // last-known-good backup Save() kept, and only then fall back to fresh defaults. Every step
    // is logged under [WIN] so the App Log Monitor shows exactly how far recovery got.
    private AppSettings RecoverFromCorrupt()
    {
        try
        {
            var quarantine = $"{_path}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Move(_path, quarantine);
            Logger.Info($"[WIN] corrupt settings.json set aside as {Path.GetFileName(quarantine)}");
        }
        // Type only, no path, no ex argument: both would put the %AppData% path (the Windows
        // username) into nexus.log - a file-IO exception's own Message embeds the full path.
        catch (Exception ex) { Logger.Error($"[WIN] failed to set aside corrupt settings.json ({ex.GetType().Name})"); }

        var fromBackup = TryReadSettings(_path + ".bak");
        if (fromBackup != null)
        {
            Logger.Info("[WIN] settings recovered from settings.json.bak");
            return fromBackup;
        }

        Logger.Info("[WIN] no usable settings backup found; falling back to defaults");
        return new AppSettings();
    }

    private void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            // Write-temp-then-replace: a crash mid-write, or the portable self-swap's freshly
            // spawned instance reading while the dying one writes, must never observe a
            // truncated settings.json. File.Replace is atomic on the same volume and keeps the
            // previous good copy as settings.json.bak, so a corrupt write is always recoverable.
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, _opts));
            if (File.Exists(_path))
                File.Replace(tmp, _path, _path + ".bak");
            else
                File.Move(tmp, _path);
        }
        // Type only, no path, no ex argument - same rule as above, and this catch is LIVE on
        // every market cycle (MarketDataService's finally saves settings), so it must be as
        // path-clean as the snapshot files it runs beside.
        catch (Exception ex) { Logger.Error($"Failed to save settings ({ex.GetType().Name})"); }
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
            if (!OwnedSet.Add(name)) return;      // already owned - no change, no disk write
            Current.OwnedBlueprints.Add(name);
        }
        else
        {
            if (!OwnedSet.Remove(name)) return;   // wasn't owned - no change, no disk write
            Current.OwnedBlueprints.RemoveAll(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
        }
        Save();
    }

    // Bulk-mark owned with a single disk write - used by the Game.log importer so a
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

    /// <summary>Records how the user wants to be shown when they export - their RSI handle or a
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

    /// <summary>Reset the local Blueprint Network identity (GUID + label + detected RSI handle) - used
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
