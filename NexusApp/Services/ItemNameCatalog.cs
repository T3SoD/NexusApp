using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace NexusApp.Services;

// Token-to-display-name table for the items Game.log names only by their internal itemName
// token (crlf_consumable_healing_01). Derived data: CIG's packed global.ini item_Name keys,
// extracted per SC patch (check_p4k_update.ps1 ritual, then gen_item_names.py); the extraction
// tooling stays local, only this table ships. Used by the wallet's purchase attribution.
public sealed class ItemNameCatalog
{
    private readonly Dictionary<string, string> _guids;
    private readonly Dictionary<string, string> _names;

    public ItemNameCatalog(Dictionary<string, string> guids, Dictionary<string, string> names)
    {
        _guids = new Dictionary<string, string>(guids, StringComparer.OrdinalIgnoreCase);
        _names = new Dictionary<string, string>(names, StringComparer.OrdinalIgnoreCase);
    }

    public int Count => _names.Count;

    /// <summary>The display name for a logged item, GUID first and token second, or null when
    /// neither map knows it. Callers fall back to the shop name rather than render a guess.
    /// The GUID is preferred because CIG's entity tokens and localization keys disagree for some
    /// items, and the entity record the GUID points at names the correct key.</summary>
    public string? Resolve(string? itemGuid, string? itemToken)
    {
        if (!string.IsNullOrWhiteSpace(itemGuid) && _guids.TryGetValue(itemGuid, out var byGuid)
            && IsRealName(byGuid)) return byGuid;
        if (!string.IsNullOrWhiteSpace(itemToken) && _names.TryGetValue(itemToken, out var byToken)
            && IsRealName(byToken)) return byToken;
        return null;
    }

    // Three shapes the extraction leaves behind that are not display names: an unresolved
    // localization pointer ("@mp_ePistol", CIG never localized that string), a placeholder the
    // source data itself ships with ("Placeholder - CBD Boots"), and CIG's own missing-entry
    // marker ("<-=MISSING=->", 18 rows in item_names.json, e.g. DebugGun). Rendering any of them
    // to the player is worse than falling back to the shop name.
    private static bool IsRealName(string name) =>
        !name.StartsWith('@') && !name.StartsWith("PLACEHOLDER", StringComparison.OrdinalIgnoreCase)
        && !name.StartsWith("<-=MISSING=->", StringComparison.Ordinal);

    // Lazy app-wide instance, the CommodityNameCatalog idiom: loaded on first attribution,
    // never on the startup path.
    private static ItemNameCatalog? _instance;
    public static ItemNameCatalog Instance => _instance ??= LoadEmbedded();

    // Per-app-run coverage-report gate for the unresolved-token line (the MarketQueries.LogMissOnce
    // / UnmatchedBlueprintLog idiom): one log line per distinct token, not one per purchase, so a
    // session with many purchases of the same unrecognized item never floods nexus.log.
    private static readonly HashSet<string> _loggedUnresolved = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Resolve a shop purchase's display name ONCE, at the point the purchase enters the
    /// ledger (ProfitTracker.Ingest), so WalletDisplay.PurchaseTitle never touches the catalog
    /// during a render. Never throws: a broken or missing embedded resource degrades to null so
    /// the caller falls back to the shop name instead of losing the row. The unresolved-token line
    /// logs once per distinct token per app run, staying a coverage report rather than a per-row
    /// flood.</summary>
    public static string? ResolvePurchaseName(string? itemGuid, string? itemToken)
    {
        string? name;
        try { name = Instance.Resolve(itemGuid, itemToken); }
        catch (Exception ex)
        {
            Logger.Info($"[WALLET] item catalog unavailable: {ex.Message}");
            return null;
        }
        if (name is not null) return name;
        lock (_loggedUnresolved)
        {
            if (!_loggedUnresolved.Add(itemToken ?? "")) return null;
        }
        Logger.Info($"[WALLET] purchase name unresolved: {itemToken}");
        return null;
    }

    public static ItemNameCatalog LoadEmbedded()
    {
        using var stream = typeof(ItemNameCatalog).Assembly
            .GetManifestResourceStream("NexusApp.Data.item_names.json")
            ?? throw new InvalidOperationException("item_names.json embedded resource not found");
        return Load(stream);
    }

    public static ItemNameCatalog Load(Stream stream)
    {
        var raw = JsonSerializer.Deserialize<RawFile>(stream)
                  ?? throw new InvalidOperationException("item_names.json deserialized to null");
        return new ItemNameCatalog(raw.Guids ?? new Dictionary<string, string>(),
                                   raw.Names ?? new Dictionary<string, string>());
    }

    private sealed class RawFile
    {
        [System.Text.Json.Serialization.JsonPropertyName("guids")]
        public Dictionary<string, string>? Guids { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("names")]
        public Dictionary<string, string>? Names { get; set; }
    }
}
