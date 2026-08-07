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
    private readonly Dictionary<string, string> _names;

    public ItemNameCatalog(Dictionary<string, string> names) =>
        _names = new Dictionary<string, string>(names, StringComparer.OrdinalIgnoreCase);

    public int Count => _names.Count;

    /// <summary>The display name for a log itemName token, or null when the table does not know
    /// it (a new item on a newer game build than the table). Callers fall back to the shop name
    /// rather than render a guess.</summary>
    public string? Resolve(string? itemToken) =>
        !string.IsNullOrWhiteSpace(itemToken) && _names.TryGetValue(itemToken, out var name)
            ? name : null;

    // Lazy app-wide instance, the CommodityNameCatalog idiom: loaded on first attribution,
    // never on the startup path.
    private static ItemNameCatalog? _instance;
    public static ItemNameCatalog Instance => _instance ??= LoadEmbedded();

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
        return new ItemNameCatalog(raw.Names ?? new Dictionary<string, string>());
    }

    private sealed class RawFile
    {
        [System.Text.Json.Serialization.JsonPropertyName("names")]
        public Dictionary<string, string>? Names { get; set; }
    }
}
