using NexusApp.Models;
using NexusApp.Views;
using Xunit;

namespace NexusApp.Tests;

public class QualityControlTests
{
    private static readonly List<StatModifier> OneMod =
        [new StatModifier("weapon_damage", [new QualityRange(0, 1000, 0.9, 1.1, null, null)])];

    private static BlueprintSlot Slot(string name, List<string> resources, List<StatModifier>? mods = null) =>
        new(name, resources, 0.02, "SCU", false, mods ?? OneMod);

    [Fact]
    public void TypedCommit_RejectsNonNumeric()
    {
        Assert.Null(QualityControl.ParseTyped(""));
        Assert.Null(QualityControl.ParseTyped("abc"));
        Assert.Null(QualityControl.ParseTyped("12345"));
        Assert.Null(QualityControl.ParseTyped("-5"));
        Assert.Equal(780, QualityControl.ParseTyped("780"));
        Assert.Equal(1000, QualityControl.ParseTyped("5000"));
        Assert.Equal(0, QualityControl.ParseTyped("0"));
    }

    [Fact]
    public void SlotForIngredient_MatchesAnyOption()
    {
        var bp = new Blueprint { Name = "Test", Slots = [Slot("Frame", ["Aluminium"]), Slot("Barrel", ["Copper", "Iron"])] };
        Assert.Equal("Barrel", MainWindow.SlotForIngredient(bp, "Iron")?.Name);
        Assert.Equal("Barrel", MainWindow.SlotForIngredient(bp, "iron")?.Name);
        Assert.Null(MainWindow.SlotForIngredient(bp, "Gold"));
    }

    [Fact]
    public void SlotForIngredient_IgnoresSlotsWithoutModifiers()
    {
        var bp = new Blueprint { Name = "Test", Slots = [Slot("Grip", ["Iron"], []), Slot("Barrel", ["Iron"])] };
        Assert.Equal("Barrel", MainWindow.SlotForIngredient(bp, "Iron")?.Name);
        var bare = new Blueprint { Name = "Bare", Slots = [Slot("Grip", ["Iron"], [])] };
        Assert.Null(MainWindow.SlotForIngredient(bare, "Iron"));
        Assert.Empty(MainWindow.SlotsForIngredient(bare, "Iron"));
    }

    [Fact]
    public void SlotForIngredient_TwoSlotsOneOre_ReturnsBoth()
    {
        var bp = new Blueprint { Name = "Test", Slots = [Slot("Frame", ["Iron"]), Slot("Stock", ["Gold"]), Slot("Barrel", ["Iron"])] };
        var slots = MainWindow.SlotsForIngredient(bp, "Iron");
        Assert.Equal(["Frame", "Barrel"], slots.Select(s => s.Name));
        Assert.Equal("6 cSCU  ·  FRAME + BARREL slots", MainWindow.SlotLine(0.06, "SCU", slots));
    }

    [Fact]
    public void SlotLine_OneSlot_Singular()
    {
        var slots = new List<BlueprintSlot> { Slot("Frame", ["Aluminium"]) };
        Assert.Equal("4 cSCU  ·  FRAME slot", MainWindow.SlotLine(0.04, "SCU", slots));
        Assert.Equal("2 units  ·  FRAME slot", MainWindow.SlotLine(2, "units", slots));
    }

    [Fact]
    public void CraftTimeText_FormatsSecondsAndMinutes()
    {
        Assert.Equal("45 s", MainWindow.CraftTimeText(45));
        Assert.Equal("3 min", MainWindow.CraftTimeText(150));
        Assert.Equal("14 min", MainWindow.CraftTimeText(810));
    }

    [Fact]
    public void PresetActive_OnlyWhenEveryQualityMatches()
    {
        Assert.True(CraftForecastStrip.PresetActive([500, 500, 500], 500));
        Assert.False(CraftForecastStrip.PresetActive([500, 780, 500], 500));
        Assert.False(CraftForecastStrip.PresetActive([1000], 500));
        Assert.False(CraftForecastStrip.PresetActive([], 500));
    }
}
