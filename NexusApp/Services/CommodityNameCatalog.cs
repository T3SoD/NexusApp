using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace NexusApp.Services;

// GUID-to-display-name table for the commodities the Game.log names only by resourceGUID
// (TODO D10a). Derived data: DataCore resourcetypedatabase records joined to the packed
// global.ini localization, extracted per SC patch (check_p4k_update.ps1 ritual); the
// extraction tooling stays local, only this table ships. 207 rows at 4.9.0; every GUID the
// owner ever transacted resolved (28 of 28, verified 2026-08-05).
public sealed class CommodityNameCatalog
{
    private readonly Dictionary<string, string> _names;

    public CommodityNameCatalog(Dictionary<string, string> names) =>
        _names = new Dictionary<string, string>(names, StringComparer.OrdinalIgnoreCase);

    public int Count => _names.Count;

    /// <summary>The display name for a log resourceGUID, or null when the table does not know
    /// it (a new commodity on a newer game build than the table). Callers render nothing
    /// rather than a guess.</summary>
    public string? Resolve(string? resourceGuid) =>
        !string.IsNullOrWhiteSpace(resourceGuid) && _names.TryGetValue(resourceGuid, out var name)
            ? name : null;

    // Lazy app-wide instance, the ContractCapCatalog idiom: loaded on first ledger render,
    // never on the startup path.
    private static CommodityNameCatalog? _instance;
    public static CommodityNameCatalog Instance => _instance ??= LoadEmbedded();

    public static CommodityNameCatalog LoadEmbedded()
    {
        using var stream = typeof(CommodityNameCatalog).Assembly
            .GetManifestResourceStream("NexusApp.Data.commodity_names.json")
            ?? throw new InvalidOperationException("commodity_names.json embedded resource not found");
        return Load(stream);
    }

    public static CommodityNameCatalog Load(Stream stream)
    {
        var raw = JsonSerializer.Deserialize<RawFile>(stream)
                  ?? throw new InvalidOperationException("commodity_names.json deserialized to null");
        return new CommodityNameCatalog(raw.Names ?? new Dictionary<string, string>());
    }

    private sealed class RawFile
    {
        [System.Text.Json.Serialization.JsonPropertyName("names")]
        public Dictionary<string, string>? Names { get; set; }
    }
}
