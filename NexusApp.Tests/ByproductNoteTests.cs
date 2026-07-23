namespace NexusApp.Tests;

using System;
using System.Collections.Generic;
using NexusApp.Models;
using NexusApp.Views;
using Xunit;

// Pure-helper coverage for the Blueprint Library "BYPRODUCT SOURCING" block (issue #12).
// The approved mock (nexus-design-lab/bp-where-to-mine/explore.html) is the source of truth:
// Section C drives the band-bar grouping and scale, Section B drives the "via <host>" chips.
// Host and band order follow the found-in order the caller passes in (the app's DB accessor
// GetFoundInForResource), so these tests feed that order explicitly.
public class ByproductNoteTests
{
    private static FoundInSource Src(string ore, double min, double max) =>
        new(ore, min, max, 0.5, 1);

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> Loc(
        params (string Host, string[] Locations)[] entries)
    {
        var d = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries) d[e.Host] = e.Locations;
        return d;
    }

    // ── Groups: band grouping data (hosts + min + max per band) ────────────────

    [Fact]
    public void Groups_SharedBand_MergesHostsAscendingByBand()
    {
        // DB found-in order for Aslarite: Agricium, Titanium (both 2-5%), then Iron (20-50%).
        var groups = ByproductNote.Groups(new[]
        {
            Src("Agricium", 2, 5),
            Src("Titanium", 2, 5),
            Src("Iron", 20, 50),
        });

        Assert.Equal(2, groups.Count);
        Assert.Equal(new[] { "Agricium", "Titanium" }, groups[0].Hosts);
        Assert.Equal(2, groups[0].Min);
        Assert.Equal(5, groups[0].Max);
        Assert.Equal(new[] { "Iron" }, groups[1].Hosts);
        Assert.Equal(20, groups[1].Min);
        Assert.Equal(50, groups[1].Max);
    }

    [Fact]
    public void Groups_SingleHost_OneGroup()
    {
        var groups = ByproductNote.Groups(new[] { Src("Iron", 20, 50) });

        Assert.Single(groups);
        Assert.Equal(new[] { "Iron" }, groups[0].Hosts);
        Assert.Equal(50, groups[0].Max);
    }

    [Fact]
    public void Groups_ThreeHostsOneBand_KeepsInputOrder()
    {
        var groups = ByproductNote.Groups(new[]
        {
            Src("Silicon", 20, 50),
            Src("Tin", 20, 50),
            Src("Ice", 20, 50),
        });

        Assert.Single(groups);
        Assert.Equal(new[] { "Silicon", "Tin", "Ice" }, groups[0].Hosts);
    }

    [Fact]
    public void Groups_Empty_ReturnsEmpty()
    {
        Assert.Empty(ByproductNote.Groups(Array.Empty<FoundInSource>()));
    }

    [Fact]
    public void Groups_OrdersAscendingByBandRegardlessOfInput()
    {
        // Richer band supplied first; grouping still lists the lower band first.
        var groups = ByproductNote.Groups(new[]
        {
            Src("Iron", 20, 50),
            Src("Titanium", 2, 5),
        });

        Assert.Equal(new[] { "Titanium" }, groups[0].Hosts);
        Assert.Equal(new[] { "Iron" }, groups[1].Hosts);
    }

    // ── BarFraction: segment width relative to the ore's richest band ──────────

    [Fact]
    public void BarFraction_FivePercentOfFifty_IsOneTenth()
    {
        // Mock rule: a 5% band renders at 5/50 of the track when 50% is the ore's max.
        Assert.Equal(0.1, ByproductNote.BarFraction(5, 50), 10);
    }

    [Fact]
    public void BarFraction_RichestBand_FillsTrack()
    {
        Assert.Equal(1.0, ByproductNote.BarFraction(50, 50), 10);
    }

    [Fact]
    public void BarFraction_ZeroOreMax_IsZero()
    {
        Assert.Equal(0.0, ByproductNote.BarFraction(5, 0));
    }

    // ── Percent: upper-bound label, trailing zeros trimmed ────────────────────

    [Fact]
    public void Percent_TrimsTrailingZeros()
    {
        Assert.Equal("50%", ByproductNote.Percent(50.0));
        Assert.Equal("5%", ByproductNote.Percent(5.0));
        Assert.Equal("2.5%", ByproductNote.Percent(2.5));
    }

    // ── HostsPresentAt: which host rocks actually spawn at a location ──────────

    [Fact]
    public void HostsPresentAt_Hit_ReturnsPresentHostsInFoundInOrder()
    {
        // Aslarite at Aberdeen: Agricium + Titanium spawn there, Iron does not.
        var chips = ByproductNote.HostsPresentAt(
            new[] { Src("Agricium", 2, 5), Src("Titanium", 2, 5), Src("Iron", 20, 50) },
            Loc(
                ("Agricium", new[] { "Aberdeen", "Yela" }),
                ("Titanium", new[] { "Aberdeen" }),
                ("Iron", new[] { "Daymar" })),
            "Aberdeen");

        Assert.True(chips.HasHosts);
        Assert.Equal(new[] { "Agricium", "Titanium" }, chips.Present);
        Assert.Equal(0, chips.Overflow);
    }

    [Fact]
    public void HostsPresentAt_Miss_HasHostsButNonePresent()
    {
        // Ouratite at Aberdeen: its only host (Iron) does not spawn there.
        var chips = ByproductNote.HostsPresentAt(
            new[] { Src("Iron", 20, 50) },
            Loc(("Iron", new[] { "Daymar" })),
            "Aberdeen");

        Assert.True(chips.HasHosts);
        Assert.Empty(chips.Present);
        Assert.Equal(0, chips.Overflow);
    }

    [Fact]
    public void HostsPresentAt_Empty_NoHosts()
    {
        // An ore with no found-in rows contributes no chips at all.
        var chips = ByproductNote.HostsPresentAt(
            Array.Empty<FoundInSource>(), Loc(), "Aberdeen");

        Assert.False(chips.HasHosts);
        Assert.Empty(chips.Present);
        Assert.Equal(0, chips.Overflow);
    }

    [Fact]
    public void HostsPresentAt_PreservesOrderAfterFilteringAbsentHost()
    {
        // Stileron at Pyro VI (Terminus): Silicon absent, Tin + Ice present, order kept.
        var chips = ByproductNote.HostsPresentAt(
            new[] { Src("Silicon", 20, 50), Src("Tin", 20, 50), Src("Ice", 20, 50) },
            Loc(
                ("Silicon", new[] { "Daymar" }),
                ("Tin", new[] { "Pyro VI (Terminus)" }),
                ("Ice", new[] { "Pyro VI (Terminus)" })),
            "Pyro VI (Terminus)");

        Assert.True(chips.HasHosts);
        Assert.Equal(new[] { "Tin", "Ice" }, chips.Present);
        Assert.Equal(0, chips.Overflow);
    }

    [Fact]
    public void HostsPresentAt_CapsAtTwoWithOverflowCount()
    {
        // Four present hosts: two chips shown, the rest folded into a "+N" count.
        var chips = ByproductNote.HostsPresentAt(
            new[] { Src("A", 1, 2), Src("B", 1, 2), Src("C", 1, 2), Src("D", 1, 2) },
            Loc(
                ("A", new[] { "L" }), ("B", new[] { "L" }),
                ("C", new[] { "L" }), ("D", new[] { "L" })),
            "L");

        Assert.Equal(new[] { "A", "B" }, chips.Present);
        Assert.Equal(2, chips.Overflow);
        Assert.True(chips.HasHosts);
    }
}
