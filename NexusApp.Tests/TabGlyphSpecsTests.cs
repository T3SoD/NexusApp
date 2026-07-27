using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

public class TabGlyphSpecsTests
{
    // Every overlay tab, plus the reserved future trade tab, must resolve to a non-empty glyph.
    [Fact]
    public void EveryTab_HasGlyphParts()
    {
        foreach (var id in OverlayTabs.Ids)
            Assert.NotEmpty(TabGlyphSpecs.PartsFor(id));
        Assert.NotEmpty(TabGlyphSpecs.PartsFor("trade"));
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
