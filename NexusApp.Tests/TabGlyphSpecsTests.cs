using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

public class TabGlyphSpecsTests
{
    // Every overlay tab must resolve to a non-empty glyph.
    [Fact]
    public void EveryTab_HasGlyphParts()
    {
        foreach (var id in OverlayTabs.Ids)
            Assert.NotEmpty(TabGlyphSpecs.PartsFor(id));
    }

    // Reported 2026-08-01: the overlay icon did not match the main app's trade icon. It drew
    // a hand-authored trending-up arrow because the tab predated the Trade page's dock icon. Only
    // shopping - which has no app page - may stay hand-authored; every other tab must resolve
    // through the dock so a regenerated pick can never leave the overlay behind. This is the guard
    // that keeps a future tab from quietly re-acquiring its own private glyph.
    [Fact]
    public void Trade_DrawsTheDockBalanceScale_NotAHandAuthoredGlyph()
    {
        var parts = TabGlyphSpecs.PartsFor("trade");

        // DockIconSpecsCustom "trade": beam post, pan bar, two hangers, two pans, one cross bar.
        Assert.Equal(7, parts.Count);
        Assert.Equal("M12 4 L12 20.5", parts[0].Attrs["d"]);
        Assert.Contains(parts, p => p.El == "path" && p.Attrs["d"] == "M1.8 12.5 A 3.2 3.2 0 0 0 8.2 12.5");
        // The retired arrow, so the fix cannot silently revert.
        Assert.DoesNotContain(parts, p => p.Attrs.TryGetValue("d", out var d) && d == "M16 7h6v6");
    }

    // Flourish parts (f_ prefix) are dock-scale decoration; the 15px tab glyphs must drop them.
    // PartsFor strips ids during parsing, so the guard is structural: no part may carry a fill
    // attribute (only the flourish pip does) and dock-mapped tabs must have the known core counts.
    [Fact]
    public void CoreParts_OnlyNoFlourishes()
    {
        Assert.Single(TabGlyphSpecs.PartsFor("stats"));           // operations pulse path (xUnit2013)
        Assert.Equal(7, TabGlyphSpecs.PartsFor("scan").Count);    // rs brackets + signal lines
        Assert.Equal(3, TabGlyphSpecs.PartsFor("orders").Count);  // refinery layers
        Assert.Equal(5, TabGlyphSpecs.PartsFor("hauling").Count); // cargo box-check
        Assert.Equal(3, TabGlyphSpecs.PartsFor("guides").Count);  // folded map
        foreach (var id in OverlayTabs.Ids)
            Assert.DoesNotContain(TabGlyphSpecs.PartsFor(id), p => p.Attrs.ContainsKey("fill"));
    }

    [Fact]
    public void Strokes_MatchDockSpecs()
    {
        Assert.Equal(1.6, TabGlyphSpecs.StrokeFor("scan"));
        Assert.Equal(1.5, TabGlyphSpecs.StrokeFor("stats"));
        Assert.Equal(1.5, TabGlyphSpecs.StrokeFor("shopping"));
    }

    [Fact]
    public void Crops_ArePositive()
    {
        foreach (var id in OverlayTabs.Ids)
        {
            var (_, _, w, h) = TabGlyphSpecs.CropFor(id);
            Assert.True(w > 0 && h > 0);
        }
    }
}
