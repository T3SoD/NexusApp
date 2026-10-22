using System.Globalization;
using NexusApp.Models;

namespace NexusApp.Services;

/// <summary>One forecast row: how a crafted item's stat compares with a stock (quality 500) craft.</summary>
public sealed record StatForecast(
    string Key,
    string Name,
    double Factor,
    int Additive,
    bool IsAdditive,
    int BetterWhen,
    double Min,
    double Max,
    IReadOnlyList<string> Slots)
{
    /// <summary>"stock", "better" or "worse".</summary>
    public string Verdict
    {
        get
        {
            double delta = IsAdditive ? Additive : Factor - 1.0;
            // Same rounding as DeltaText, so the verdict and the printed text always agree.
            bool stock = IsAdditive ? Additive == 0 : Math.Round((Factor - 1.0) * 100.0, 1) == 0;
            if (stock) return "stock";
            return Math.Sign(delta) == BetterWhen ? "better" : "worse";
        }
    }

    /// <summary>"+2.8%", "-10.0%", "0.0%" for stock, or "+3 pips" / "-1 pip" for additive stats.</summary>
    public string DeltaText
    {
        get
        {
            if (IsAdditive)
            {
                string unit = Math.Abs(Additive) == 1 ? "pip" : "pips";
                string sign = Additive >= 0 ? "+" : "-";
                return $"{sign}{Math.Abs(Additive).ToString(CultureInfo.InvariantCulture)} {unit}";
            }
            double pct = Math.Round((Factor - 1.0) * 100.0, 1, MidpointRounding.AwayFromZero);
            if (pct == 0) return "0.0%";
            return (pct > 0 ? "+" : "-") + Math.Abs(pct).ToString("0.0", CultureInfo.InvariantCulture) + "%";
        }
    }
}

/// <summary>Pure forecast logic for the craft quality calculator. No UI, no I/O.</summary>
public static class CraftForecast
{
    public const int StockQuality = 500;

    /// <summary>Maps a held quality through a resource band table. An empty table passes the raw value.</summary>
    public static int Quantize(IReadOnlyList<QualityBand> bands, int quality)
    {
        foreach (var b in bands)
            if (b.Start <= quality && quality <= b.End) return b.Mapped;
        return quality;
    }

    /// <summary>Multiplicative value at a quantized quality, or null when the modifier is additive or empty.</summary>
    public static double? EvaluateFactor(StatModifier m, int quantized)
    {
        var r = PickRange(m, quantized);
        if (r == null || r.IsAdditive) return null;
        return FactorAt(r, quantized);
    }

    /// <summary>Flat additive step at a quantized quality, or null when the modifier is multiplicative or empty.</summary>
    public static int? EvaluateAdditive(StatModifier m, int quantized)
    {
        var r = PickRange(m, quantized);
        if (r == null || !r.IsAdditive) return null;
        return r.A0;
    }

    /// <summary>
    /// ASSUMPTION A: per-slot factors on one stat add as deltas, 1 + sum(f - 1).
    /// An in-game check may show they multiply instead. This is the only place the rule lives.
    /// </summary>
    public static double Combine(IEnumerable<double> factors) => 1.0 + factors.Sum(f => f - 1.0);

    /// <summary>The alternative rule (product). Not used by <see cref="Forecast"/>.</summary>
    public static double CombineProduct(IEnumerable<double> factors)
    {
        double p = 1.0;
        foreach (var f in factors) p *= f;
        return p;
    }

    public static IReadOnlyList<StatForecast> Forecast(
        Blueprint bp,
        Func<string, int> heldQualityOf,
        Func<string, IReadOnlyList<QualityBand>> bandsOf,
        IReadOnlyDictionary<string, CraftStat> stats)
    {
        var order = new List<string>();
        var contributions = new Dictionary<string, List<(string Slot, StatModifier Mod, int Mine, int Stock)>>();

        foreach (var slot in bp.Slots)
        {
            IReadOnlyList<QualityBand> bands = slot.Resources.Count > 0
                ? bandsOf(slot.Resources[0])
                : Array.Empty<QualityBand>();
            int mine = Quantize(bands, heldQualityOf(slot.Name));
            int stock = Quantize(bands, StockQuality);
            foreach (var mod in slot.Modifiers)
            {
                if (mod.Ranges.Count == 0) continue;
                if (!contributions.TryGetValue(mod.Stat, out var list))
                {
                    list = [];
                    contributions[mod.Stat] = list;
                    order.Add(mod.Stat);
                }
                list.Add((slot.Name, mod, mine, stock));
            }
        }

        var result = new List<StatForecast>();
        foreach (var key in order)
        {
            var items = contributions[key];
            bool additive = items.Any(i => i.Mod.Ranges.Any(r => r.IsAdditive));

            string name = key;
            if (stats.TryGetValue(key, out var cs))
            {
                name = cs.Name;
                if (cs.Override != null
                    && bp.SubCategory == "Mining Laser"
                    && cs.Override.TryGetValue("WeaponMining", out var ov))
                    name = ov;
            }

            var slotNames = items.Select(i => i.Slot).Distinct().ToList();

            // Direction: sum over modifiers of (value at highest Q1 minus value at lowest Q0).
            double slope = 0;
            foreach (var i in items)
            {
                var lo = i.Mod.Ranges.OrderBy(r => r.Q0).First();
                var hi = i.Mod.Ranges.OrderByDescending(r => r.Q1).First();
                slope += Value(hi, hi.Q1) - Value(lo, lo.Q0);
            }
            int betterWhen = slope >= 0 ? 1 : -1;

            if (additive)
            {
                int mine = items.Sum(i => EvaluateAdditive(i.Mod, i.Mine) ?? 0);
                int stock = items.Sum(i => EvaluateAdditive(i.Mod, i.Stock) ?? 0);
                int min = items.Sum(i => (int)i.Mod.Ranges.Min(r => Extreme(r, true)));
                int max = items.Sum(i => (int)i.Mod.Ranges.Max(r => Extreme(r, false)));
                result.Add(new StatForecast(key, name, 1.0, mine - stock, true, betterWhen, min - stock, max - stock, slotNames));
            }
            else
            {
                double mine = Combine(items.Select(i => EvaluateFactor(i.Mod, i.Mine) ?? 1.0));
                double stock = Combine(items.Select(i => EvaluateFactor(i.Mod, i.Stock) ?? 1.0));
                double min = Combine(items.Select(i => i.Mod.Ranges.Min(r => Extreme(r, true))));
                double max = Combine(items.Select(i => i.Mod.Ranges.Max(r => Extreme(r, false))));
                result.Add(new StatForecast(key, name, mine / stock, 0, false, betterWhen, min / stock, max / stock, slotNames));
            }
        }
        return result;
    }

    private static QualityRange? PickRange(StatModifier m, int q)
    {
        QualityRange? best = null;
        int bestDist = int.MaxValue;
        foreach (var r in m.Ranges)
        {
            int dist = q < r.Q0 ? r.Q0 - q : q > r.Q1 ? q - r.Q1 : 0;
            if (dist < bestDist)
            {
                best = r;
                bestDist = dist;
                if (dist == 0) break;
            }
        }
        return best;
    }

    // Value of a range at q, clamped to the range's own interval.
    private static double FactorAt(QualityRange r, int q)
    {
        double m0 = r.M0 ?? 1.0, m1 = r.M1 ?? m0;
        if (r.Q1 == r.Q0) return m0;
        int c = Math.Clamp(q, r.Q0, r.Q1);
        return m0 + (m1 - m0) * (c - r.Q0) / (r.Q1 - r.Q0);
    }

    private static double Value(QualityRange r, int q) =>
        r.IsAdditive ? r.A0!.Value : FactorAt(r, q);

    // Extreme endpoint of a range (M0/M1 or A0/A1), used for Min and Max.
    private static double Extreme(QualityRange r, bool min)
    {
        double a = r.IsAdditive ? r.A0!.Value : r.M0 ?? 1.0;
        double b = r.IsAdditive ? r.A1 ?? r.A0!.Value : r.M1 ?? a;
        return min ? Math.Min(a, b) : Math.Max(a, b);
    }
}
