using NexusApp.Services.Cargo;
using Xunit;
using Item = NexusApp.Services.Cargo.CargoSignoffStore.ReviewItem;

namespace NexusApp.Tests.Cargo;

public class CargoSignoffStoreTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"signoff_test_{Guid.NewGuid():N}.json");

    [Fact]
    public void Unreviewed_ByDefault()
    {
        var s = new CargoSignoffStore(TempPath());
        Assert.False(s.IsChecked("drak-ironclad", Item.Hologram));
        Assert.False(s.IsFullySignedOff("drak-ironclad"));
        Assert.Equal(0, s.CheckedCount("drak-ironclad"));
        Assert.Equal(0, s.CountFullySignedOff());
    }

    [Fact]
    public void Checks_Persist_AcrossReload()
    {
        var path = TempPath();
        var s = new CargoSignoffStore(path);
        s.SetCheck("aegs-idris-p", "Idris-P", Item.GridPosition, true);
        s.SetCheck("aegs-idris-p", "Idris-P", Item.Hologram, true);

        var reloaded = new CargoSignoffStore(path);
        Assert.True(reloaded.IsChecked("aegs-idris-p", Item.GridPosition));
        Assert.True(reloaded.IsChecked("aegs-idris-p", Item.Hologram));
        Assert.False(reloaded.IsChecked("aegs-idris-p", Item.GridProperties));
        Assert.Equal(2, reloaded.CheckedCount("aegs-idris-p"));
        Assert.False(reloaded.IsFullySignedOff("aegs-idris-p"));
        Assert.NotNull(reloaded.When("aegs-idris-p"));
        File.Delete(path);
    }

    [Fact]
    public void AllThreeChecked_IsFullySignedOff()
    {
        var s = new CargoSignoffStore(TempPath());
        foreach (var item in CargoSignoffStore.Items)
            s.SetCheck("drak-ironclad", "Ironclad", item, true);
        Assert.True(s.IsFullySignedOff("drak-ironclad"));
        Assert.Equal(1, s.CountFullySignedOff());
    }

    [Fact]
    public void Flag_IsIndependent_OfChecks()
    {
        var path = TempPath();
        var s = new CargoSignoffStore(path);
        s.SetFlagged("railen", "Railen", true);
        Assert.True(s.IsFlagged("railen"));
        Assert.Equal(1, s.CountFlagged());
        Assert.False(s.IsFullySignedOff("railen"));

        var reloaded = new CargoSignoffStore(path);
        Assert.True(reloaded.IsFlagged("railen"));
        File.Delete(path);
    }

    [Fact]
    public void ClearChecks_ResetsGivenItems()
    {
        var path = TempPath();
        var s = new CargoSignoffStore(path);
        foreach (var item in CargoSignoffStore.Items)
            s.SetCheck("cutter", "Cutter", item, true);

        s.ClearChecks("cutter", "Cutter", Item.GridPosition, Item.GridProperties);
        Assert.False(s.IsChecked("cutter", Item.GridPosition));
        Assert.False(s.IsChecked("cutter", Item.GridProperties));
        Assert.True(s.IsChecked("cutter", Item.Hologram));   // untouched
        File.Delete(path);
    }

    [Fact]
    public void UncheckingEverything_RemovesEntry()
    {
        var s = new CargoSignoffStore(TempPath());
        s.SetCheck("cutter", "Cutter", Item.Hologram, true);
        s.SetCheck("cutter", "Cutter", Item.Hologram, false);
        Assert.Equal(0, s.CheckedCount("cutter"));
        Assert.Null(s.When("cutter"));
    }
}
