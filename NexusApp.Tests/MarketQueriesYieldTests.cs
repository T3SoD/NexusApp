using System;
using System.Collections.Generic;
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// App review G8: refineries_yields had been fetched, parsed, cached and persisted since the market
// feature shipped, and read by nothing. These cover the query that finally reads it, including the
// two joins that make it usable at all - UEX's terminal naming, and the RAW commodity key.
public class MarketQueriesYieldTests
{
    // ---- RefineryStationName ------------------------------------------------------------------
    // UEX: "Refinement Center - Levski". The seed: "Levski". Nothing joins without this.

    [Theory]
    [InlineData("Refinement Center - Levski", "Levski")]
    [InlineData("Refinement Processing - Stanton Gateway (Pyro)", "Stanton Gateway (Pyro)")]
    [InlineData("Refinement Center - Nyx Gateway (Pyro)", "Nyx Gateway (Pyro)")]
    public void RefineryStationName_StripsTheUexPrefix(string uex, string expected)
        => Assert.Equal(expected, MarketQueries.RefineryStationName(uex));

    [Fact]
    public void RefineryStationName_NameWithNoSeparator_IsReturnedUnchanged()
        => Assert.Equal("Orbituary", MarketQueries.RefineryStationName("Orbituary"));

    [Fact]
    public void RefineryStationName_KeepsLaterSeparators()
    {
        // Only the FIRST " - " is the prefix boundary. A station whose own name contained one
        // would otherwise lose half of itself.
        Assert.Equal("Some Place - Annex", MarketQueries.RefineryStationName("Refinement Center - Some Place - Annex"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void RefineryStationName_NullOrBlank_ReturnsEmpty_NeverThrows(string? name)
        => Assert.Equal("", MarketQueries.RefineryStationName(name));

    // ---- LiveYieldsByStation ------------------------------------------------------------------

    private const int RawIronId = 45;
    private const int RefinedIronId = 46;

    private static MarketSnapshot Snap(params MarketYieldRow[] yields)
    {
        var s = new MarketSnapshot();
        s.Commodities.Rows = new List<MarketCommodity>
        {
            new(RawIronId, "Iron (Ore)", "iron-ore", IsRaw: true, IsRefined: false, IdParent: 0),
            new(RefinedIronId, "Iron", "iron", IsRaw: false, IsRefined: true, IdParent: RawIronId),
        };
        s.Yields.Rows = new List<MarketYieldRow>(yields);
        return s;
    }

    private static MarketYieldRow Row(int commodityId, string terminalName, int bonus, int terminalId = 1) =>
        new(terminalId, commodityId, bonus, bonus, new DateTime(2026, 5, 15, 6, 30, 0, DateTimeKind.Utc), terminalName);

    [Fact]
    public void LiveYieldsByStation_KeysOnTheStationNameTheSeedUses()
    {
        var hits = MarketQueries.LiveYieldsByStation(Snap(Row(RawIronId, "Refinement Center - Levski", -5)), "Iron");

        Assert.True(hits.ContainsKey("Levski"));
        Assert.Equal(-5, hits["Levski"].BonusPct);
    }

    [Fact]
    public void LiveYieldsByStation_LookupIsCaseInsensitive()
    {
        var hits = MarketQueries.LiveYieldsByStation(Snap(Row(RawIronId, "Refinement Center - Levski", 7)), "Iron");
        Assert.Equal(7, hits["levski"].BonusPct);
    }

    // The join that is easy to get backwards. A refining bonus applies to what goes INTO the
    // refinery, so these rows are keyed on the RAW commodity - unlike every price query in this
    // class, which follows the raw-to-refined link. Keying on the refined id would silently return
    // nothing for every ore, and an empty result is indistinguishable from "nobody reported it".
    [Fact]
    public void LiveYieldsByStation_ReadsTheRawCommodity_NotTheRefinedOne()
    {
        var snap = Snap(Row(RawIronId, "Refinement Center - Levski", 7),
                        Row(RefinedIronId, "Refinement Center - Orbituary", 99));

        var hits = MarketQueries.LiveYieldsByStation(snap, "Iron");

        Assert.True(hits.ContainsKey("Levski"));
        Assert.False(hits.ContainsKey("Orbituary"));
    }

    [Fact]
    public void LiveYieldsByStation_IgnoresOtherOres()
    {
        var hits = MarketQueries.LiveYieldsByStation(Snap(Row(commodityId: 999, "Refinement Center - Levski", 7)), "Iron");
        Assert.Empty(hits);
    }

    [Fact]
    public void LiveYieldsByStation_CarriesTheReportsOwnDate_SoAgeCanBeShown()
    {
        var hits = MarketQueries.LiveYieldsByStation(Snap(Row(RawIronId, "Refinement Center - Levski", -5)), "Iron");
        Assert.Equal(new DateTime(2026, 5, 15, 6, 30, 0, DateTimeKind.Utc), hits["Levski"].ModifiedUtc);
    }

    [Fact]
    public void LiveYieldsByStation_DuplicateRowsForOneStation_KeepTheFirst()
    {
        // UEX reports one row per terminal per commodity. A duplicate means upstream changed shape,
        // and silently overwriting would hide that; first-wins at least stays deterministic.
        var hits = MarketQueries.LiveYieldsByStation(
            Snap(Row(RawIronId, "Refinement Center - Levski", 3), Row(RawIronId, "Refinement Center - Levski", 8)), "Iron");

        Assert.Equal(3, hits["Levski"].BonusPct);
    }

    [Fact]
    public void LiveYieldsByStation_NoSnapshot_IsEmpty_NotNull()
        => Assert.Empty(MarketQueries.LiveYieldsByStation(null, "Iron"));

    [Fact]
    public void LiveYieldsByStation_UnmappedOre_IsEmpty()
        => Assert.Empty(MarketQueries.LiveYieldsByStation(Snap(Row(RawIronId, "Refinement Center - Levski", 5)),
                                                          "Not A Real Ore Name"));

    [Fact]
    public void LiveYieldsByStation_NoReportedRows_IsEmpty()
        => Assert.Empty(MarketQueries.LiveYieldsByStation(Snap(), "Iron"));
}
