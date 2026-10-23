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
    public void SlotsForIngredient_MatchesAnyOption()
    {
        var bp = new Blueprint { Name = "Test", Slots = [Slot("Frame", ["Aluminium"]), Slot("Barrel", ["Copper", "Iron"])] };
        Assert.Equal("Barrel", MainWindow.SlotsForIngredient(bp, "Iron").FirstOrDefault()?.Name);
        Assert.Equal("Barrel", MainWindow.SlotsForIngredient(bp, "iron").FirstOrDefault()?.Name);
        Assert.Null(MainWindow.SlotsForIngredient(bp, "Gold").FirstOrDefault());
    }

    [Fact]
    public void SlotsForIngredient_IgnoresSlotsWithoutModifiers()
    {
        var bp = new Blueprint { Name = "Test", Slots = [Slot("Grip", ["Iron"], []), Slot("Barrel", ["Iron"])] };
        Assert.Equal("Barrel", MainWindow.SlotsForIngredient(bp, "Iron").FirstOrDefault()?.Name);
        var bare = new Blueprint { Name = "Bare", Slots = [Slot("Grip", ["Iron"], [])] };
        Assert.Null(MainWindow.SlotsForIngredient(bare, "Iron").FirstOrDefault());
        Assert.Empty(MainWindow.SlotsForIngredient(bare, "Iron"));
    }

    [Fact]
    public void SlotsForIngredient_TwoSlotsOneOre_ReturnsBoth()
    {
        var bp = new Blueprint { Name = "Test", Slots = [Slot("Frame", ["Iron"]), Slot("Stock", ["Gold"]), Slot("Barrel", ["Iron"])] };
        var slots = MainWindow.SlotsForIngredient(bp, "Iron");
        Assert.Equal(["Frame", "Barrel"], slots.Select(s => s.Name));
    }

    [Fact]
    public void CraftTimeText_FormatsSecondsAndMinutes()
    {
        Assert.Equal("45 s", MainWindow.CraftTimeText(45));
        Assert.Equal("1 min 30 s", MainWindow.CraftTimeText(90));
        Assert.Equal("2 min", MainWindow.CraftTimeText(120));
        Assert.Equal("2 min 30 s", MainWindow.CraftTimeText(150));
    }

    [Fact]
    public void PresetActive_OnlyWhenEveryQualityMatches()
    {
        Assert.True(CraftForecastStrip.PresetActive([500, 500, 500], 500));
        Assert.False(CraftForecastStrip.PresetActive([500, 780, 500], 500));
        Assert.False(CraftForecastStrip.PresetActive([1000], 500));
        Assert.False(CraftForecastStrip.PresetActive([], 500));
    }

    [Fact]
    public void WithOpacity_FreezesABrushWhoseColorIsAResourceReference()
    {
        // Brushes are DispatcherObjects; build them on an STA thread like the UI does.
        Exception? failure = null;
        var t = new Thread(() =>
        {
            try
            {
                // Same shape as the theme's FgBrush: Color bound with DynamicResource, no resource tree.
                var src = new System.Windows.Media.SolidColorBrush();
                src.SetValue(System.Windows.Media.SolidColorBrush.ColorProperty,
                    new System.Windows.DynamicResourceExtension("FgColor").ProvideValue(null!));
                Assert.False(src.Clone().CanFreeze);   // the trap: Clone keeps the resource expression

                var b = Hud.WithOpacity(src, 0.75);
                Assert.True(b.IsFrozen);
                Assert.Equal(0.75, b.Opacity);
            }
            catch (Exception ex) { failure = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [Fact]
    public void LayoutFor_SplitsAt700()
    {
        Assert.Equal(MainWindow.DetailLayout.Stacked, MainWindow.LayoutFor(0));
        Assert.Equal(MainWindow.DetailLayout.Stacked, MainWindow.LayoutFor(699.9));
        Assert.Equal(MainWindow.DetailLayout.Wide, MainWindow.LayoutFor(700));
        Assert.Equal(MainWindow.DetailLayout.Wide, MainWindow.LayoutFor(1340));
    }

    [Fact]
    public void StackRowControl_Below560()
    {
        Assert.True(MainWindow.StackRowControl(413));
        Assert.True(MainWindow.StackRowControl(559.9));
        Assert.False(MainWindow.StackRowControl(560));
        Assert.False(MainWindow.StackRowControl(658));
    }

    [Fact]
    public void RowWidthFor_HalvesTheSplitOnlyWhenWide()
    {
        Assert.Equal(413, MainWindow.RowWidthFor(850));
        Assert.Equal(650, MainWindow.RowWidthFor(650));
    }
}
