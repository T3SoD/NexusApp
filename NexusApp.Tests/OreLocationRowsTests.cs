using NexusApp.Models;
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

public class OreLocationRowsTests
{
    private static OreSpawn P(string body, double share, int packMin = 0, int packMax = 0) =>
        new(body, "Stanton", SpawnKind.Primary, share, 0, 0, 0, Array.Empty<string>(), packMin, packMax);

    private static OreSpawn G(string body, SpawnKind kind, double share) =>
        new(body, "Stanton", kind, share, 0, 0, 0, Array.Empty<string>(), 0, 0);

    private static OreSpawn B(string body, double min, double max, params string[] hosts) =>
        new(body, "Stanton", SpawnKind.Byproduct, null, min, max, 0.5, hosts, 0, 0);

    private static string Sys(string _) => "Stanton";

    [Fact]
    public void Build_OrdersPrimaryByShareThenGemsThenByproductThenPlain()
    {
        var spawns = new[]
        {
            P("Lyria", 10), P("Daymar", 28.5), G("Cellin", SpawnKind.Hand, 40),
            B("Yela", 1, 5, "Quartzite"), B("Arial", 2, 10, "Shale"),
        };
        var locations = new[] { "Arial", "Cellin", "Daymar", "Hathor Caves", "Lyria", "Yela" };

        var rows = OreLocationRows.Build(locations, spawns, Sys);

        Assert.Equal(new[] { "Daymar", "Lyria", "Cellin", "Arial", "Yela", "Hathor Caves" },
            rows.Select(r => r.Body).ToArray());
        Assert.Null(rows[^1].Kind);
    }

    [Fact]
    public void Build_EveryLocationAppearsOnce_AndSpawnNotInLocationsStillEmitted()
    {
        var spawns = new[] { P("Daymar", 20), P("Extra Body", 5) };
        var locations = new[] { "Daymar", "Lyria" };

        var rows = OreLocationRows.Build(locations, spawns, Sys);

        Assert.Equal(1, rows.Count(r => r.Body == "Daymar"));
        Assert.Equal(1, rows.Count(r => r.Body == "Lyria"));
        Assert.Equal(1, rows.Count(r => r.Body == "Extra Body"));
        Assert.Equal(3, rows.Count);
    }

    [Fact]
    public void Build_UnweightedRow_HasNoFillNoValueNoTooltip()
    {
        var rows = OreLocationRows.Build(new[] { "Hathor Caves" }, Array.Empty<OreSpawn>(), n => "Sys:" + n);

        var row = Assert.Single(rows);
        Assert.Null(row.Kind);
        Assert.Equal(0, row.FillPct);
        Assert.Equal("", row.ValueText);
        Assert.Empty(row.Hosts);
        Assert.Equal("", row.Tooltip);
        Assert.Equal("Sys:Hathor Caves", row.System);
    }

    [Fact]
    public void Build_BodyWithGemAndByproductSpawns_EmitsOneRowPerKind()
    {
        var spawns = new[]
        {
            new OreSpawn("Cellin", "Stanton", SpawnKind.Hand, 40, 0, 0, 0, Array.Empty<string>(), 0, 0),
            new OreSpawn("Cellin", "Stanton", SpawnKind.Byproduct, null, 0, 5, 0.5, new[] { "Iron" }, 0, 0),
        };

        var rows = OreLocationRows.Build(new[] { "Cellin" }, spawns, Sys);

        var cellin = rows.Where(r => r.Body == "Cellin").ToList();
        Assert.Equal(2, cellin.Count);
        Assert.Equal(SpawnKind.Hand, cellin[0].Kind);
        Assert.Equal(SpawnKind.Byproduct, cellin[1].Kind);
        Assert.DoesNotContain(rows, r => r.Body == "Cellin" && r.Kind is null);
    }

    [Fact]
    public void FormatShare_DropsTrailingZero()
    {
        Assert.Equal("34.8%", OreLocationRows.FormatShare(34.8));
        Assert.Equal("2%", OreLocationRows.FormatShare(2.0));
        Assert.Equal("10%", OreLocationRows.FormatShare(10.0));
    }

    [Fact]
    public void FormatBand_RoundsToInts()
    {
        Assert.Equal("2-5%", OreLocationRows.FormatBand(2.0, 5.0));
        Assert.Equal("5-10%", OreLocationRows.FormatBand(4.6, 10.4));
    }

    [Fact]
    public void Tooltip_Primary_IncludesPackRange_And_Byproduct_ListsHosts()
    {
        var rows = OreLocationRows.Build(new[] { "Aaron Halo", "Single", "NoPack", "Arial" },
            new[]
            {
                P("Aaron Halo", 18, 2, 4), P("Single", 9, 3, 3), P("NoPack", 4),
                B("Arial", 2, 5, "Shale", "Quartzite"),
            }, Sys);

        Assert.Equal("18% of Aaron Halo's ship-rock spawns, packs of 2-4", rows.Single(r => r.Body == "Aaron Halo").Tooltip);
        Assert.Equal("9% of Single's ship-rock spawns, packs of 3", rows.Single(r => r.Body == "Single").Tooltip);
        Assert.Equal("4% of NoPack's ship-rock spawns", rows.Single(r => r.Body == "NoPack").Tooltip);
        Assert.Equal("Carried inside Shale, Quartzite rocks at 2-5% of the rock", rows.Single(r => r.Body == "Arial").Tooltip);
    }

    [Fact]
    public void FillPct_IsShareForPrimary_MaxPctForByproduct()
    {
        var rows = OreLocationRows.Build(new[] { "Daymar", "Arial" },
            new[] { P("Daymar", 28.5), B("Arial", 2, 10, "Shale") }, Sys);

        Assert.Equal(28.5, rows.Single(r => r.Body == "Daymar").FillPct);
        Assert.Equal(10, rows.Single(r => r.Body == "Arial").FillPct);
        Assert.Equal("28.5%", rows.Single(r => r.Body == "Daymar").ValueText);
        Assert.Equal("2-10%", rows.Single(r => r.Body == "Arial").ValueText);
    }
}
