using System.Globalization;
using NexusApp.Models;

namespace NexusApp.Services;

/// <summary>One display row in an ore's location list. Kind is null for an unweighted
/// community row (no fill, no value, no hosts, no tooltip).</summary>
public record OreLocationRow(string Body, string System, SpawnKind? Kind, double FillPct, string ValueText,
    IReadOnlyList<string> Hosts, string Tooltip);

/// <summary>Pure ordering and formatting seam for the Codex location rows. No UI types.</summary>
public static class OreLocationRows
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Builds the ordered rows: Primary by share, then ROC and hand gems by share, then
    /// Byproduct by max percent, then unweighted locations alphabetically. A spawn whose body is
    /// not in <paramref name="locations"/> is still emitted. systemOf is used for unweighted rows only.</summary>
    public static IReadOnlyList<OreLocationRow> Build(IReadOnlyList<string> locations,
        IReadOnlyList<OreSpawn> spawns, Func<string, string> systemOf)
    {
        var rows = new List<OreLocationRow>();

        rows.AddRange(spawns.Where(s => s.Kind == SpawnKind.Primary)
            .OrderByDescending(s => s.Share ?? 0).ThenBy(s => s.Body, StringComparer.OrdinalIgnoreCase)
            .Select(ToRow));
        rows.AddRange(spawns.Where(s => s.Kind is SpawnKind.Roc or SpawnKind.Hand)
            .OrderByDescending(s => s.Share ?? 0).ThenBy(s => s.Body, StringComparer.OrdinalIgnoreCase)
            .Select(ToRow));
        rows.AddRange(spawns.Where(s => s.Kind == SpawnKind.Byproduct)
            .OrderByDescending(s => s.MaxPct).ThenBy(s => s.Body, StringComparer.OrdinalIgnoreCase)
            .Select(ToRow));

        var weighted = new HashSet<string>(spawns.Select(s => s.Body), StringComparer.OrdinalIgnoreCase);
        rows.AddRange(locations.Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(l => !weighted.Contains(l))
            .OrderBy(l => l, StringComparer.OrdinalIgnoreCase)
            .Select(l => new OreLocationRow(l, systemOf(l), null, 0, "", Array.Empty<string>(), "")));

        return rows;
    }

    /// <summary>Formats a percent share, dropping a trailing ".0" (34.8 -> "34.8%", 2.0 -> "2%").</summary>
    public static string FormatShare(double share) =>
        Math.Round(share, 1, MidpointRounding.AwayFromZero).ToString("0.#", Inv) + "%";

    /// <summary>Formats a percent band with both ends rounded to integers (2,5 -> "2-5%").</summary>
    public static string FormatBand(double min, double max) =>
        $"{Math.Round(min, MidpointRounding.AwayFromZero).ToString("0", Inv)}-{Math.Round(max, MidpointRounding.AwayFromZero).ToString("0", Inv)}%";

    /// <summary>Upper-case tag text for a spawn kind.</summary>
    public static string TagText(SpawnKind kind) => kind switch
    {
        SpawnKind.Primary => "PRIMARY",
        SpawnKind.Byproduct => "BYPRODUCT",
        SpawnKind.Roc => "ROC",
        SpawnKind.Hand => "HAND",
        _ => "",
    };

    private static OreLocationRow ToRow(OreSpawn s)
    {
        if (s.Kind == SpawnKind.Byproduct)
        {
            var band = FormatBand(s.MinPct, s.MaxPct);
            return new OreLocationRow(s.Body, s.System, s.Kind, s.MaxPct, band, s.Hosts,
                $"Carried inside {string.Join(", ", s.Hosts)} rocks at {band} of the rock");
        }

        var share = s.Share ?? 0;
        var text = FormatShare(share);
        var tip = s.Kind switch
        {
            SpawnKind.Primary => $"{text} of {s.Body}'s ship-rock spawns" + PackClause(s),
            SpawnKind.Roc => $"{text} of {s.Body}'s ROC-gem spawns",
            _ => $"{text} of {s.Body}'s hand-gem spawns",
        };
        return new OreLocationRow(s.Body, s.System, s.Kind, share, text, s.Hosts, tip);
    }

    private static string PackClause(OreSpawn s)
    {
        if (s.PackMax == 0) return "";
        return s.PackMin == s.PackMax
            ? $", packs of {s.PackMin}"
            : $", packs of {s.PackMin}-{s.PackMax}";
    }
}
