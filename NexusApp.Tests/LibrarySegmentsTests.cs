using NexusApp.Models;
using NexusApp.Services;
using NexusApp.Views;
using Xunit;

namespace NexusApp.Tests;

public class LibrarySegmentsTests
{
    private static StatForecast Mult(double factor, double min = 0.925, double max = 1.075) =>
        new("k", "Stat", factor, 0, false, 1, min, max, ["Frame"]);

    private static StatForecast Add(int additive, double min, double max) =>
        new("k", "Stat", 1.0, additive, true, 1, min, max, ["Frame"]);

    [Fact]
    public void DefaultSegment_OwnedOpensCrafting()
    {
        Assert.Equal("crafting", MainWindow.DefaultSegment(true));
        Assert.Equal("sourcing", MainWindow.DefaultSegment(false));
    }

    [Fact]
    public void ShowSegments_RequiresControlsAndRows()
    {
        Assert.True(MainWindow.ShowSegments(true, 5));
        Assert.False(MainWindow.ShowSegments(true, 0));
        Assert.False(MainWindow.ShowSegments(false, 5));
    }

    [Fact]
    public void BarFraction_ScalesAgainstTheWiderSide()
    {
        Assert.Equal(1.0, CraftStatRows.BarFraction(Mult(1.075)), 6);
        Assert.Equal(0.0, CraftStatRows.BarFraction(Mult(1.0)), 6);
        Assert.Equal(0.5, CraftStatRows.BarFraction(Mult(0.9625)), 6);
        Assert.Equal(0.5, CraftStatRows.BarFraction(Add(2, 0, 4)), 6);
        Assert.Equal(0.0, CraftStatRows.BarFraction(Mult(1.0, 1.0, 1.0)), 6);
    }

    [Fact]
    public void BarGrowsRight_FollowsTheSign()
    {
        Assert.True(CraftStatRows.BarGrowsRight(Mult(1.02)));
        Assert.False(CraftStatRows.BarGrowsRight(Mult(0.98)));
        Assert.False(CraftStatRows.BarGrowsRight(Add(-1, -4, 4)));
    }

    [Fact]
    public void TierLine_NamesTheSlotsTheOreFeeds()
    {
        static BlueprintSlot Slot(string name) => new(name, ["Iron"], 0.02, "SCU", false, []);
        Assert.Equal("Common", MainWindow.TierLine("Common", []));
        Assert.Equal("Common · Frame", MainWindow.TierLine("Common", [Slot("Frame")]));
        Assert.Equal("Common · Frame + Stock", MainWindow.TierLine("Common", [Slot("Frame"), Slot("Stock")]));
    }

    [Fact]
    public void SlotLabel_DropsTheQuantity()
    {
        static BlueprintSlot Slot(string name) => new(name, ["Iron"], 0.02, "SCU", false, []);
        Assert.Equal("FRAME slot", MainWindow.SlotLabel([Slot("Frame")]));
        Assert.Equal("FRAME + BARREL slots", MainWindow.SlotLabel([Slot("Frame"), Slot("Barrel")]));
    }
}
