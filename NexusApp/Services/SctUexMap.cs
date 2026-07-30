using System.IO;
using System.Text.Json;

namespace NexusApp.Services;

// Curated SCT-shop-path <-> UEX-id correspondence (Data/sct_uex_map.json), authored on the
// unmerged recon/sct-uex-mapping branch (docs/superpowers/specs/2026-07-29-sct-uex-divergence-
// benchmark.md). Role-split per station: UEX models a station's shops as separate TERMINALS
// (Admin / Refinery Ore Sales / Platinum Bay), SCT mostly models the STATION - so each terminal
// entry carries CommodityId (refined/general trade) and RawId (refinery ore sales), and a caller
// routes by which one the traded commodity actually is. A terminal with neither id is SCT-only
// (Note explains why); a commodity with a null UexId is an SCT-only item name. Public-safe: name
// correspondences only, no bulk source data, no PII - never hand-edit this file or its source JSON.
internal sealed record SctTerminalMapping(int? CommodityId, int? RawId, string? Note);
internal sealed record SctCommodityMapping(int? UexId, string? Note);

internal sealed class SctUexMap
{
    public IReadOnlyDictionary<string, SctTerminalMapping> Terminals { get; }
    public IReadOnlyDictionary<string, SctCommodityMapping> Commodities { get; }

    private SctUexMap(Dictionary<string, SctTerminalMapping> terminals,
                      Dictionary<string, SctCommodityMapping> commodities)
    {
        Terminals = terminals;
        Commodities = commodities;
    }

    // NexusApp.Data.<filename> - the same embedded-resource naming CargoShipCatalog.LoadEmbedded
    // uses for Data/cargo_ships.json (folder separators become dots).
    private const string ResourceName = "NexusApp.Data.sct_uex_map.json";

    public static SctUexMap LoadEmbedded()
    {
        using var stream = typeof(SctUexMap).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource not found: {ResourceName}");
        return Load(stream);
    }

    // Never throws: a malformed stream (should not happen for an embedded, build-controlled
    // resource, but this loader is also exercised directly in tests) resolves to an empty map
    // rather than crashing whatever called it.
    public static SctUexMap Load(Stream stream)
    {
        try
        {
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var raw = JsonSerializer.Deserialize<RawMap>(stream, opts) ?? new RawMap();

            var terminals = new Dictionary<string, SctTerminalMapping>(StringComparer.OrdinalIgnoreCase);
            if (raw.Terminals is not null)
                foreach (var (path, entry) in raw.Terminals)
                    terminals[path] = new SctTerminalMapping(entry.CommodityId, entry.RawId, entry.Note);

            var commodities = new Dictionary<string, SctCommodityMapping>(StringComparer.OrdinalIgnoreCase);
            if (raw.Commodities is not null)
                foreach (var (name, entry) in raw.Commodities)
                    commodities[name] = new SctCommodityMapping(entry.UexId, entry.Note);

            return new SctUexMap(terminals, commodities);
        }
        catch (Exception)
        {
            return new SctUexMap(
                new Dictionary<string, SctTerminalMapping>(StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, SctCommodityMapping>(StringComparer.OrdinalIgnoreCase));
        }
    }

    // _meta (uexOnlyTerminals list, generation notes) is intentionally not modeled: nothing in
    // the trading tab reads it, and System.Text.Json simply ignores JSON properties that have no
    // matching member on RawMap, so skipping it costs nothing.
    private sealed class RawMap
    {
        public Dictionary<string, RawTerminalEntry>? Terminals { get; set; }
        public Dictionary<string, RawCommodityEntry>? Commodities { get; set; }
    }

    private sealed class RawTerminalEntry
    {
        public int? CommodityId { get; set; }
        public int? RawId { get; set; }
        public string? Note { get; set; }
    }

    private sealed class RawCommodityEntry
    {
        public int? UexId { get; set; }
        public string? Note { get; set; }
    }
}
