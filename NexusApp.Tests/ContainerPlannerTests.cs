using System.Linq;
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// Container menus below are real values from the shipped market snapshot; the awkward ones
// (16,24,32 with no 8; 8,16 only) were read out of the owner's own Game.log at two Pyro shops,
// where the same commodity had different menus at different terminals.
public class ContainerPlannerTests
{
    private static int Total(ContainerPlan p) => p.Picks.Sum(x => x.Scu * x.Count);

    [Fact]
    public void FillsExactly_WhenTheMenuDivides()
    {
        var p = ContainerPlanner.Plan("1,2,4,8,16,24,32", shipMaxContainerScu: 32, targetScu: 696)!;
        Assert.True(p.HitsTarget);
        Assert.Equal(696, p.TotalScu);
        Assert.Equal(696, Total(p));
    }

    [Fact]
    public void PrefersFewestCrates_ForTheSameTotal()
    {
        // 696 is reachable as 87x8 or as 21x32 + 1x24. Loading time and the per-crate auto-load fee
        // both scale with count, so the 22-crate answer must win over the 87-crate one.
        var p = ContainerPlanner.Plan("1,2,4,8,16,24,32", 32, 696)!;
        Assert.Equal(22, p.BoxCount);
        Assert.Equal(new[] { 32, 24 }, p.Picks.Select(x => x.Scu).ToArray());
        Assert.Equal(21, p.Picks.First(x => x.Scu == 32).Count);
    }

    [Fact]
    public void RespectsTheShipsMaxContainer_NotJustTheKiosksMenu()
    {
        // A Cutlass Black tops out at 16 SCU crates, so the 24s and 32s this kiosk sells are
        // unusable to it even though the kiosk lists them.
        var p = ContainerPlanner.Plan("1,2,4,8,16,24,32", shipMaxContainerScu: 16, targetScu: 46)!;
        Assert.All(p.Picks, x => Assert.True(x.Scu <= 16));
        Assert.Equal(46, p.TotalScu);
    }

    [Fact]
    public void ReportsTheShortfall_WhenTheMenuCannotReachTheTarget()
    {
        // The owner's original complaint, reproduced: 46 SCU of capacity against a kiosk whose
        // smallest crate is 8 leaves 6 SCU unbuyable.
        var p = ContainerPlanner.Plan("8,16,24,32", shipMaxContainerScu: 16, targetScu: 46)!;
        Assert.False(p.HitsTarget);
        Assert.Equal(40, p.TotalScu);
        Assert.Equal(6, p.ShortfallScu);
        Assert.Equal(40, Total(p));
    }

    [Fact]
    public void HandlesAMenuWithNoCommonSmallCrate()
    {
        // 24 and 32 have gcd 8, but 8, 16 and 40 are NOT reachable from them. A gcd-based shortcut
        // would claim 40 here; the exact solver must answer 32.
        var p = ContainerPlanner.Plan("24,32", shipMaxContainerScu: 32, targetScu: 40)!;
        Assert.Equal(32, p.TotalScu);
        Assert.Equal(8, p.ShortfallScu);
        Assert.Equal(1, p.BoxCount);
    }

    [Fact]
    public void CombinesTwoSizes_WhenNeitherAloneReachesTheTarget()
    {
        // 16+24 = 40 exactly, from a menu with no 8.
        var p = ContainerPlanner.Plan("16,24,32", shipMaxContainerScu: 32, targetScu: 40)!;
        Assert.True(p.HitsTarget);
        Assert.Equal(2, p.BoxCount);
        Assert.Equal(new[] { 24, 16 }, p.Picks.Select(x => x.Scu).ToArray());
    }

    [Fact]
    public void ReturnsAnEmptyPlan_WhenTheSmallestCrateExceedsTheTarget()
    {
        // Not an error and not null: "this kiosk cannot sell you anything that small" is a real,
        // displayable answer. Clamping it to a fake single crate would overfill the ship.
        var p = ContainerPlanner.Plan("16,24,32", shipMaxContainerScu: 32, targetScu: 12)!;
        Assert.True(p.Empty);
        Assert.Equal(0, p.TotalScu);
        Assert.Equal(12, p.ShortfallScu);
    }

    [Fact]
    public void ReturnsNull_WhenNoCrateFitsTheShip()
    {
        // A Vulture (max 8) at a kiosk selling only 16 and up: there is nothing to plan.
        Assert.Null(ContainerPlanner.Plan("16,24,32", shipMaxContainerScu: 8, targetScu: 12));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-8")]
    public void ReturnsNull_ForAnUnusableMenu(string menu)
    {
        // Fail closed, matching TradeMath.BoxFits: an unknown menu is never guessed at.
        Assert.Null(ContainerPlanner.Plan(menu, 32, 100));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ReturnsNull_ForANonPositiveTarget(int target)
    {
        Assert.Null(ContainerPlanner.Plan("1,2,4,8", 32, target));
    }

    [Fact]
    public void ReturnsNull_WhenTheShipHasNoUsableContainerSize()
    {
        Assert.Null(ContainerPlanner.Plan("1,2,4,8", shipMaxContainerScu: 0, targetScu: 8));
    }

    [Fact]
    public void ToleratesAMessyMenuString()
    {
        // Whitespace, an empty entry, a duplicate and a junk token: the usable menu is {8,16,32},
        // so 56 is 32 + 16 + 8.
        var p = ContainerPlanner.Plan(" 8 ,, 16,16 ,x, 32 ", 32, 56)!;
        Assert.True(p.HitsTarget);
        Assert.Equal(3, p.BoxCount);
        Assert.Equal(new[] { 32, 16, 8 }, p.Picks.Select(x => x.Scu).ToArray());
    }

    [Fact]
    public void HandlesTheLargestHullInTheCatalog()
    {
        // Hull C, 4,608 SCU. Guards the DP's cost and its arithmetic at the top of the range.
        var p = ContainerPlanner.Plan("1,2,4,8,16,24,32", 32, 4608)!;
        Assert.True(p.HitsTarget);
        Assert.Equal(144, p.BoxCount);          // 4608 / 32
        Assert.Single(p.Picks);
        Assert.Equal(32, p.Picks[0].Scu);
    }

    [Fact]
    public void ReportsTheUsableRange_FilteredByTheShipNotJustTheMenu()
    {
        // The menu offers 1..32 but this hull tops out at 16, so 16 is the usable max. Reporting
        // the menu's own 32 would advertise a container the ship cannot load, and would contradict
        // the picks underneath it - which is exactly why the range comes off the plan.
        var p = ContainerPlanner.Plan("1,2,4,8,16,24,32", shipMaxContainerScu: 16, targetScu: 46)!;
        Assert.Equal(1, p.MinContainerScu);
        Assert.Equal(16, p.MaxContainerScu);
        Assert.False(p.SingleSize);
        Assert.All(p.Picks, x => Assert.InRange(x.Scu, p.MinContainerScu, p.MaxContainerScu));
    }

    [Fact]
    public void ReportsTheUsableRange_WhenTheTerminalIsTheBindingLimit()
    {
        // Ship can take 32, terminal's biggest is 16: the terminal is what caps the run.
        var p = ContainerPlanner.Plan("8,16", shipMaxContainerScu: 32, targetScu: 96)!;
        Assert.Equal(8, p.MinContainerScu);
        Assert.Equal(16, p.MaxContainerScu);
    }

    [Fact]
    public void SingleSize_WhenOnlyOneSizeIsUsable()
    {
        // A one-size menu must not render as "16 / 16 SCU".
        var p = ContainerPlanner.Plan("16,24,32", shipMaxContainerScu: 16, targetScu: 64)!;
        Assert.True(p.SingleSize);
        Assert.Equal(16, p.MinContainerScu);
        Assert.Equal(16, p.MaxContainerScu);
    }

    [Fact]
    public void PicksAreOrderedLargestFirst()
    {
        var p = ContainerPlanner.Plan("1,2,4,8,16,24,32", 32, 700)!;
        var sizes = p.Picks.Select(x => x.Scu).ToList();
        Assert.Equal(sizes.OrderByDescending(x => x).ToList(), sizes);
    }

    [Fact]
    public void TotalAlwaysMatchesThePicks()
    {
        // Property check across every menu in the shipped snapshot's vocabulary and a spread of
        // targets: the headline number must never disagree with the crates listed under it.
        string[] menus =
        {
            "1,2,4,8,16,24,32", "1,2,4,8,16", "1,2,4,8,16,24", "8,16,24,32", "1,2,4,8",
            "1,2", "4,8,16", "8,16", "4,8,16,24", "1", "4,8,16,24,32", "16,24,32", "1,2,4",
            "8", "16", "24,32", "4", "2",
        };
        foreach (var menu in menus)
            foreach (var target in new[] { 1, 2, 7, 8, 12, 40, 46, 66, 96, 174, 696, 2200 })
                foreach (var maxBox in new[] { 1, 2, 4, 8, 16, 24, 32 })
                {
                    var p = ContainerPlanner.Plan(menu, maxBox, target);
                    if (p is null) continue;
                    Assert.Equal(p.TotalScu, Total(p));
                    Assert.Equal(target - p.TotalScu, p.ShortfallScu);
                    Assert.True(p.TotalScu <= target, $"{menu} / max {maxBox} / target {target} overfilled");
                    Assert.All(p.Picks, x => Assert.True(x.Scu <= maxBox));
                    // The advertised range must always bound the containers actually recommended.
                    Assert.True(p.MinContainerScu <= p.MaxContainerScu);
                    Assert.True(p.MaxContainerScu <= maxBox);
                    Assert.All(p.Picks, x => Assert.InRange(x.Scu, p.MinContainerScu, p.MaxContainerScu));
                }
    }

    [Fact]
    public void BuyableScu_ReturnsTheTarget_WhenTheMenuReachesItExactly()
    {
        // A menu containing 1 can hit any target, so the snap is a no-op.
        Assert.Equal(46, ContainerPlanner.BuyableScu("1,2,4,8,16,24,32", shipMaxContainerScu: 16, targetScu: 46));
    }

    [Fact]
    public void BuyableScu_ReturnsTheFullestReachableLoad_WhenTheMenuCannotReachTheTarget()
    {
        // A Cutlass Black's 46 SCU against a menu starting at 8: 5x8 = 40 is the fullest load
        // at or under the target, so 6 SCU are unbuyable.
        Assert.Equal(40, ContainerPlanner.BuyableScu("8,16,24,32", shipMaxContainerScu: 16, targetScu: 46));
    }

    [Fact]
    public void BuyableScu_ReturnsZero_WhenEveryContainerIsBiggerThanTheTarget()
    {
        Assert.Equal(0, ContainerPlanner.BuyableScu("8,16,24,32", shipMaxContainerScu: 32, targetScu: 6));
    }

    [Fact]
    public void BuyableScu_ReturnsTheTargetUnchanged_WhenNothingCanBePlanned()
    {
        // Fail-open, deliberately. An unparseable menu or a zero-capacity ship is a "cannot
        // measure", and a route must never be made to look worse because the planner could not run.
        // RoutePlanner's BoxFits gate makes both cases unreachable from ranking anyway.
        Assert.Equal(46, ContainerPlanner.BuyableScu("", shipMaxContainerScu: 16, targetScu: 46));
        Assert.Equal(46, ContainerPlanner.BuyableScu("1,2,4", shipMaxContainerScu: 0, targetScu: 46));
    }

    [Fact]
    public void BuyableScu_ReturnsZero_ForANonPositiveTarget()
    {
        Assert.Equal(0, ContainerPlanner.BuyableScu("1,2,4,8", shipMaxContainerScu: 16, targetScu: 0));
    }

    [Fact]
    public void BuyableScu_AgreesWithPlansTotal()
    {
        // The one invariant that matters: the number ranking uses and the containers the card
        // lists are the same decision, so they can never contradict each other on screen.
        var plan = ContainerPlanner.Plan("8,16,24,32", 16, 46)!;
        Assert.Equal(plan.TotalScu, ContainerPlanner.BuyableScu("8,16,24,32", 16, 46));
    }
}
