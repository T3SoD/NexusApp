using System.Text.Json;
using NexusApp.Models;
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

public class CraftForecastTests
{
    private static List<QualityBand> B(params int[][] t) => t.Select(x => new QualityBand(x[0], x[1], x[2])).ToList();

    private static readonly List<QualityBand> Iron = B(
        [0, 399, 325], [400, 599, 521], [600, 699, 664], [700, 799, 710], [800, 899, 874], [900, 949, 907], [950, 998, 970], [999, 1000, 1000]);
    private static readonly List<QualityBand> Aluminium = B(
        [0, 399, 318], [400, 599, 511], [600, 699, 614], [700, 799, 783], [800, 899, 896], [900, 949, 919], [950, 998, 953], [999, 1000, 1000]);
    private static readonly List<QualityBand> Hephestanite = B(
        [0, 399, 330], [400, 599, 572], [600, 699, 692], [700, 799, 758], [800, 899, 896], [900, 949, 916], [950, 998, 975], [999, 1000, 1000]);
    private static readonly List<QualityBand> Borase = B(
        [0, 399, 359], [400, 599, 584], [600, 699, 676], [700, 799, 743], [800, 899, 892], [900, 949, 903], [950, 998, 976], [999, 1000, 1000]);
    private static readonly List<QualityBand> Laranite = B(
        [0, 399, 298], [400, 599, 510], [600, 699, 698], [700, 799, 707], [800, 899, 858], [900, 949, 910], [950, 998, 975], [999, 1000, 1000]);
    private static readonly List<QualityBand> Beryl = B(
        [0, 399, 324], [400, 599, 547], [600, 699, 677], [700, 799, 717], [800, 899, 860], [900, 949, 937], [950, 998, 955], [999, 1000, 1000]);

    private static readonly Dictionary<string, List<QualityBand>> AllBands = new()
    {
        ["Iron"] = Iron, ["Aluminium"] = Aluminium, ["Hephestanite"] = Hephestanite,
        ["Borase"] = Borase, ["Laranite"] = Laranite, ["Beryl"] = Beryl,
    };

    private static IReadOnlyList<QualityBand> BandsOf(string r) =>
        AllBands.TryGetValue(r, out var b) ? b : new List<QualityBand>();

    private static QualityRange Lin(double m0, double m1) => new(0, 1000, m0, m1, null, null);
    private static QualityRange Add(int q0, int q1, int a) => new(q0, q1, null, null, a, a);
    private static StatModifier Mod(string stat, params QualityRange[] r) => new(stat, r.ToList());

    private static readonly Dictionary<string, CraftStat> Stats = new()
    {
        ["weapon_damage"] = new("weapon_damage", "Impact Force", new() { ["WeaponMining"] = "Laser Power" }),
        ["weapon_firerate"] = new("weapon_firerate", "Fire Rate", null),
        ["weapon_recoil_smoothness"] = new("weapon_recoil_smoothness", "Recoil Smoothness", null),
        ["weapon_recoil_handling"] = new("weapon_recoil_handling", "Recoil Handling", null),
        ["weapon_recoil_kick"] = new("weapon_recoil_kick", "Recoil Kick", null),
        ["health_maxhealth"] = new("health_maxhealth", "Integrity", null),
        ["itemresource_powergeneration"] = new("itemresource_powergeneration", "Power Pips", null),
    };

    private static List<StatModifier> Recoil() =>
    [
        Mod("weapon_recoil_smoothness", Lin(1.2, 0.8)),
        Mod("weapon_recoil_handling", Lin(1.2, 0.8)),
        Mod("weapon_recoil_kick", Lin(1.2, 0.8)),
    ];

    private static Blueprint P4Ar(string? sub = null) => new()
    {
        Name = "P4-AR", Category = "Weapons", SubCategory = sub,
        Slots =
        [
            new("Frame", ["Aluminium"], 0.04, "SCU", false, Recoil()),
            new("Stock", ["Hephestanite"], 0.02, "SCU", false, Recoil()),
            new("Barrel", ["Iron"], 0.02, "SCU", false,
                [Mod("weapon_damage", Lin(0.925, 1.075)), Mod("weapon_firerate", Lin(0.88, 1.12))]),
        ],
    };

    private static Blueprint LumaCore() => new()
    {
        Name = "LumaCore", Category = "Ship Components", SubCategory = "Power Plant",
        Slots =
        [
            new("Shell", ["Borase"], 0.1, "SCU", false,
                [Mod("health_maxhealth", new QualityRange(0, 500, 0.8, 1.0, null, null), new QualityRange(501, 1000, 1.0, 1.2, null, null))]),
            new("Voltage Regulator", ["Laranite"], 0.1, "SCU", false,
                [Mod("itemresource_powergeneration", Add(0, 249, -2), Add(250, 499, -1), Add(500, 699, 0), Add(700, 899, 1), Add(900, 1000, 2))]),
            new("Stator Cores", ["Beryl"], 0.1, "SCU", false,
                [Mod("itemresource_powergeneration", Add(0, 399, -1), Add(400, 899, 0), Add(900, 1000, 1))]),
        ],
    };

    [Fact]
    public void Quantize_Iron780_Is710() => Assert.Equal(710, CraftForecast.Quantize(Iron, 780));

    [Fact]
    public void Quantize_NoBands_PassesRaw() => Assert.Equal(780, CraftForecast.Quantize(new List<QualityBand>(), 780));

    [Fact]
    public void Evaluate_Linear_Interpolates()
    {
        var v = CraftForecast.EvaluateFactor(Mod("weapon_damage", Lin(0.925, 1.075)), 710);
        Assert.Equal(1.0315, v!.Value, 6);
    }

    [Fact]
    public void Evaluate_GapBetweenPieces_ClampsToNearestEdge()
    {
        var m = Mod("x", new QualityRange(0, 499, 0.8, 0.9, null, null), new QualityRange(501, 1000, 1.0, 1.2, null, null));
        var v = CraftForecast.EvaluateFactor(m, 500);
        Assert.Equal(0.9, v!.Value, 9);
    }

    [Fact]
    public void Evaluate_Additive_IsFlatStep()
    {
        var m = LumaCore().Slots[1].Modifiers[0];
        Assert.Equal(2, CraftForecast.EvaluateAdditive(m, 1000));
        Assert.Null(CraftForecast.EvaluateFactor(m, 1000));
    }

    [Fact]
    public void Combine_AddsDeltas() => Assert.Equal(0.7192, CraftForecast.Combine([0.8192, 0.9]), 9);

    [Fact]
    public void CombineProduct_Multiplies() => Assert.Equal(0.73728, CraftForecast.CombineProduct([0.8192, 0.9]), 9);

    [Fact]
    public void Forecast_P4AR_Barrel780_ImpactForce_Plus2_8_FireRate_Plus4_5_RecoilStock()
    {
        var rows = CraftForecast.Forecast(P4Ar(), s => s == "Barrel" ? 780 : 500, BandsOf, Stats);
        Assert.Equal("+2.8%", rows.Single(r => r.Key == "weapon_damage").DeltaText);
        Assert.Equal("+4.5%", rows.Single(r => r.Key == "weapon_firerate").DeltaText);
        var recoil = rows.Where(r => r.Key.StartsWith("weapon_recoil")).ToList();
        Assert.Equal(3, recoil.Count);
        Assert.All(recoil, r => Assert.Equal("stock", r.Verdict));
        Assert.Equal("better", rows.Single(r => r.Key == "weapon_damage").Verdict);
        Assert.Equal(new[] { "Barrel" }, rows.Single(r => r.Key == "weapon_damage").Slots);
        Assert.Equal(new[] { "Frame", "Stock" }, rows.Single(r => r.Key == "weapon_recoil_kick").Slots);
    }

    [Fact]
    public void Forecast_TwoSlotsSameStat_UsesAdditiveDeltas()
    {
        var rows = CraftForecast.Forecast(P4Ar(), s => s == "Barrel" ? 500 : 1000, BandsOf, Stats);
        var kick = rows.Single(r => r.Key == "weapon_recoil_kick");

        // Both slots quantize 1000 -> 1000 (factor 0.8). Stock quantizes to 511 and 572.
        double F(int q) => 1.2 + (0.8 - 1.2) * q / 1000.0;
        double[] mine = [F(1000), F(1000)];
        double[] stock = [F(511), F(572)];
        string Text(double f)
        {
            double pct = Math.Round((f - 1) * 100, 1);
            return (pct > 0 ? "+" : pct < 0 ? "-" : "") + Math.Abs(pct).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "%";
        }
        string additive = Text(CraftForecast.Combine(mine) / CraftForecast.Combine(stock));
        string product = Text(CraftForecast.CombineProduct(mine) / CraftForecast.CombineProduct(stock));

        Assert.NotEqual(additive, product);
        Assert.Equal(additive, kick.DeltaText);
        Assert.NotEqual(product, kick.DeltaText);
    }

    [Fact]
    public void Verdict_AgreesWithDeltaText_NearZero()
    {
        var row = new StatForecast("k", "n", 0.9996, 0, false, 1, 0.5, 1.5, ["S"]);
        Assert.Equal("0.0%", row.DeltaText);
        Assert.Equal("stock", row.Verdict);
    }

    [Fact]
    public void Forecast_LumaCore_All1000_Plus3Pips_Integrity16_1()
    {
        var rows = CraftForecast.Forecast(LumaCore(), _ => 1000, BandsOf, Stats);
        var pips = rows.Single(r => r.Key == "itemresource_powergeneration");
        Assert.Equal("+3 pips", pips.DeltaText);
        Assert.True(pips.IsAdditive);
        Assert.Equal("better", pips.Verdict);
        Assert.Equal("+16.1%", rows.Single(r => r.Key == "health_maxhealth").DeltaText);
        Assert.Equal("Power Pips", pips.Name);
    }

    [Fact]
    public void Forecast_AllStock_EveryStatIsZero_ForEverySeededBlueprint()
    {
        using var doc = SeedTestFixture.LoadSeed();
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        // The seed stores each band as a [start, end, mapped] triple and each stat without its key.
        var bands = doc.RootElement.GetProperty("qualityBands").EnumerateObject().ToDictionary(
            p => p.Name,
            p => p.Value.EnumerateArray()
                .Select(t => new QualityBand(t[0].GetInt32(), t[1].GetInt32(), t[2].GetInt32())).ToList());
        var stats = doc.RootElement.GetProperty("craftStats").EnumerateObject().ToDictionary(
            p => p.Name,
            p => new CraftStat(p.Name, p.Value.GetProperty("name").GetString() ?? p.Name,
                p.Value.GetProperty("override").Deserialize<Dictionary<string, string>>(opts)));
        IReadOnlyList<QualityBand> BandsFor(string r) => bands.TryGetValue(r, out var b) ? b : new List<QualityBand>();

        int checkedBlueprints = 0;
        int checkedRows = 0;
        foreach (var bpEl in doc.RootElement.GetProperty("blueprints").EnumerateArray())
        {
            if (!bpEl.TryGetProperty("slots", out var slotsEl)) continue;
            var slots = slotsEl.Deserialize<List<BlueprintSlot>>(opts)!;
            if (slots.Count == 0) continue;
            var bp = new Blueprint
            {
                Name = bpEl.GetProperty("name").GetString() ?? "",
                SubCategory = bpEl.TryGetProperty("subCategory", out var sc) && sc.ValueKind == JsonValueKind.String ? sc.GetString() : null,
                Slots = slots,
            };
            var rows = CraftForecast.Forecast(bp, _ => CraftForecast.StockQuality, BandsFor, stats);
            foreach (var r in rows)
            {
                Assert.True(r.DeltaText is "0.0%" or "+0 pips", $"{bp.Name} {r.Key} read {r.DeltaText}");
                Assert.Equal("stock", r.Verdict);
                checkedRows++;
            }
            checkedBlueprints++;
        }
        Assert.True(checkedBlueprints > 0);
        Assert.True(checkedRows > 1000, $"only {checkedRows} rows checked");
    }

    [Fact]
    public void Forecast_RecoilFalling_IsBetter()
    {
        var rows = CraftForecast.Forecast(P4Ar(), s => s == "Frame" ? 1000 : 500, BandsOf, Stats);
        var kick = rows.Single(r => r.Key == "weapon_recoil_kick");
        Assert.Equal(-1, kick.BetterWhen);
        Assert.True(kick.Factor < 1);
        Assert.Equal("better", kick.Verdict);
    }

    [Fact]
    public void Forecast_StatNameOverride_MiningLaserReadsLaserPower()
    {
        var mining = CraftForecast.Forecast(P4Ar("Mining Laser"), _ => 500, BandsOf, Stats);
        Assert.Equal("Laser Power", mining.Single(r => r.Key == "weapon_damage").Name);
        var normal = CraftForecast.Forecast(P4Ar("Rifle"), _ => 500, BandsOf, Stats);
        Assert.Equal("Impact Force", normal.Single(r => r.Key == "weapon_damage").Name);
        var missing = CraftForecast.Forecast(P4Ar(), _ => 500, BandsOf, new Dictionary<string, CraftStat>());
        Assert.Equal("weapon_damage", missing.Single(r => r.Key == "weapon_damage").Name);
    }
}
