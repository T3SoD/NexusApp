using System;
using System.Linq;
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// Commodity type-or-browse picker (issue #41): CommoditySuggest.Filter is the pure seam behind
// CommodityPickerBox on the Sell and Prices flows. These tests pin the empty-query "browse
// everything" rule, the case-insensitive substring match, the OrdinalIgnoreCase ordering, and the
// absence of any result cap, all independent of any WPF control.
public class CommoditySuggestTests
{
    private static readonly string[] Names = { "Agricium", "Gold", "Golden Medmon", "Laranite", "Quantanium" };

    [Fact]
    public void Filter_EmptyQuery_ReturnsFullList()
    {
        var result = CommoditySuggest.Filter(Names, "");
        Assert.Equal(Names, result);
    }

    [Fact]
    public void Filter_WhitespaceQuery_ReturnsFullList()
    {
        var result = CommoditySuggest.Filter(Names, "   ");
        Assert.Equal(Names, result);
    }

    [Fact]
    public void Filter_Substring_MatchesCaseInsensitive()
    {
        var result = CommoditySuggest.Filter(Names, "gOLd");
        Assert.Equal(new[] { "Gold", "Golden Medmon" }, result);
    }

    [Fact]
    public void Filter_Substring_MatchesMidWord()
    {
        var result = CommoditySuggest.Filter(Names, "ani");
        Assert.Equal(new[] { "Laranite", "Quantanium" }, result);
    }

    [Fact]
    public void Filter_UnsortedInput_ReturnsOrdinalIgnoreCaseOrder()
    {
        var unsorted = new[] { "quantanium", "Agricium", "Gold" };
        var result = CommoditySuggest.Filter(unsorted, "");
        Assert.Equal(new[] { "Agricium", "Gold", "quantanium" }, result);
    }

    [Fact]
    public void Filter_QueryIsTrimmedBeforeMatching()
    {
        var result = CommoditySuggest.Filter(Names, "  gold  ");
        Assert.Equal(new[] { "Gold", "Golden Medmon" }, result);
    }

    [Fact]
    public void Filter_NoMatches_ReturnsEmpty()
    {
        Assert.Empty(CommoditySuggest.Filter(Names, "zzz"));
    }

    [Fact]
    public void Filter_EmptyNames_ReturnsEmpty()
    {
        Assert.Empty(CommoditySuggest.Filter(new string[0], ""));
    }

    [Fact]
    public void Filter_NoResultCap_EveryMatchReturned()
    {
        // The old inline Sell picker capped at Take(8); the shared popup scrolls instead, so the
        // seam must never truncate.
        var many = Enumerable.Range(0, 60).Select(i => $"Commodity {i:d2}").ToArray();
        var result = CommoditySuggest.Filter(many, "Commodity");
        Assert.Equal(60, result.Count);
    }

    // pinnedFirst: the planner's "ANY" sentinel (owner's revision to issue #41: the planner field
    // behaves like the Sell field, so its no-constraint row rides inside the same popup). Pinned
    // above the alphabetical order in browse mode, matched like any name when typing, and never
    // part of the names list itself.

    [Fact]
    public void Filter_PinnedFirst_BrowseMode_LeadsTheFullList()
    {
        var result = CommoditySuggest.Filter(Names, "", pinnedFirst: "ANY");
        Assert.Equal("ANY", result[0]);
        Assert.Equal(Names, result.Skip(1));
    }

    [Fact]
    public void Filter_PinnedFirst_MatchingQuery_LeadsTheMatches()
    {
        // "an" hits the pinned "ANY" case-insensitively and the two names containing it.
        var result = CommoditySuggest.Filter(Names, "an", pinnedFirst: "ANY");
        Assert.Equal(new[] { "ANY", "Laranite", "Quantanium" }, result);
    }

    [Fact]
    public void Filter_PinnedFirst_NonMatchingQuery_IsExcluded()
    {
        var result = CommoditySuggest.Filter(Names, "gold", pinnedFirst: "ANY");
        Assert.Equal(new[] { "Gold", "Golden Medmon" }, result);
    }

    // pinned LIST generalization (R2 Task A: Task B's start picker needs ANY and LIVE pinned
    // together, in that order, above the priced terminal names). The old single-sentinel
    // pinnedFirst overload keeps working (bridged onto the list overload) and every case above
    // stays green unmodified - these tests only pin down the NEW multi-row behavior.

    [Fact]
    public void Filter_PinnedList_BrowseMode_KeepsGivenOrder()
    {
        var result = CommoditySuggest.Filter(Names, "", pinned: new[] { "ANY", "LIVE" });
        Assert.Equal(new[] { "ANY", "LIVE" }, result.Take(2));
        Assert.Equal(Names, result.Skip(2));
    }

    [Fact]
    public void Filter_PinnedList_TypingMode_IncludesOnlyMatchingPinnedInGivenOrder()
    {
        // "an" matches the pinned "ANY" case-insensitively but not "LIVE", plus the two names
        // that contain "an" - the matching pinned row must still lead, in the given order.
        var result = CommoditySuggest.Filter(Names, "an", pinned: new[] { "ANY", "LIVE" });
        Assert.Equal(new[] { "ANY", "Laranite", "Quantanium" }, result);
    }

    [Fact]
    public void Filter_PinnedList_TypingMode_ExcludesEveryNonMatchingPinnedRow()
    {
        var result = CommoditySuggest.Filter(Names, "gold", pinned: new[] { "ANY", "LIVE" });
        Assert.Equal(new[] { "Gold", "Golden Medmon" }, result);
    }

    [Fact]
    public void Filter_NullPinnedList_SameAsNoPinned()
    {
        var result = CommoditySuggest.Filter(Names, "", pinned: null);
        Assert.Equal(Names, result);
    }

    [Fact]
    public void Filter_EmptyPinnedList_SameAsNoPinned()
    {
        var result = CommoditySuggest.Filter(Names, "", pinned: Array.Empty<string>());
        Assert.Equal(Names, result);
    }

    [Fact]
    public void Filter_SinglePinnedList_MatchesOldPinnedFirstBehavior()
    {
        var viaList = CommoditySuggest.Filter(Names, "an", pinned: new[] { "ANY" });
        var viaLegacy = CommoditySuggest.Filter(Names, "an", pinnedFirst: "ANY");
        Assert.Equal(viaLegacy, viaList);
    }
}
