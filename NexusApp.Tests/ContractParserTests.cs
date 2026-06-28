using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

public class ContractParserTests
{
    // Covalex single-commodity (screenshot 1).
    private const string Covalex =
        "Opportunity for Independent Cargo Hauler | Patch City > Checkmate [100 Rep]\n" +
        "Reward  119,000\nContract Deadline N/A\nContracted By Covalex Independent Contractors\n" +
        "DETAILS\nEarning such accolades as 'Imperial Finances' Top 10 ...\n" +
        "PRIMARY OBJECTIVES\n" +
        "Deliver 0/6 SCU of Medical Supplies to Checkmate at the L4 Lagrange of Pyro II.\n" +
        "Collect Medical Supplies from Patch City.\n";

    // Ling multi-commodity (screenshot 2).
    private const string Ling =
        "Cargo Hauling Opportunity with Ling Hauling | Chawla's Beach > Rayari Kaltag Research\n" +
        "Reward 169,250\nContract Deadline N/A\nContracted By Ling Family Hauling\n" +
        "PRIMARY OBJECTIVES\n" +
        "Deliver 0/4 SCU of Sunset Berries to Rayari Kaltag Research Outpost on Calliope.\n" +
        "Collect Sunset Berries from Chawla's Beach.\n" +
        "Deliver 0/2 SCU of Golden Medmon to Rayari Kaltag Research Outpost on Calliope.\n" +
        "Collect Golden Medmon from Chawla's Beach.\n";

    // CFP "Need a Hauler" (screenshot 3) - the log can't supply this cargo.
    private const string Cfp =
        "Need a Hauler [50/200 Rep]\nReward 139,250\nContract Deadline N/A\n" +
        "Contracted By Citizens For Prosperity\nDETAILS\nHi, we need to move some cargo ...\n" +
        "PRIMARY OBJECTIVES\n" +
        "Deliver 0/6 SCU of Hydrogen to Ruin Station above Pyro VI.\n" +
        "Collect Hydrogen from Jackson's Swap.\n";

    [Fact]
    public void Parse_Covalex_AllFields()
    {
        var d = ContractParser.Parse(Covalex);
        Assert.NotNull(d);
        Assert.Contains("Patch City > Checkmate", d!.Title);
        Assert.Equal(119000, d.Reward);
        Assert.Equal("Covalex Independent Contractors", d.ContractedBy);
        var o = Assert.Single(d.Objectives);
        Assert.Equal("Medical Supplies", o.Commodity);
        Assert.Equal(6, o.Scu);
        Assert.Equal("Patch City", o.Pickup);
        Assert.StartsWith("Checkmate", o.Dropoff);
    }

    [Fact]
    public void Parse_Ling_TwoObjectives()
    {
        var d = ContractParser.Parse(Ling);
        Assert.Equal(169250, d!.Reward);
        Assert.Equal(2, d.Objectives.Count);
        Assert.Contains(d.Objectives, o => o.Commodity == "Sunset Berries" && o.Scu == 4 && o.Pickup == "Chawla's Beach");
        Assert.Contains(d.Objectives, o => o.Commodity == "Golden Medmon" && o.Scu == 2);
    }

    [Fact]
    public void Parse_Cfp_FillsCargoTheLogLacks()
    {
        var d = ContractParser.Parse(Cfp);
        Assert.Equal("Citizens For Prosperity", d!.ContractedBy);
        var o = Assert.Single(d.Objectives);
        Assert.Equal("Hydrogen", o.Commodity);
        Assert.Equal(6, o.Scu);
        Assert.Equal("Jackson's Swap", o.Pickup);
        Assert.StartsWith("Ruin Station", o.Dropoff);
    }

    [Fact]
    public void Parse_NonContractText_ReturnsNull()
    {
        Assert.Null(ContractParser.Parse("HOME HEALTH COMMS CONTRACTS MAPS\nsome unrelated screen text"));
    }

    [Theory]
    [InlineData("Need a Hauler [50/200 Rep]", "need a hauler")]
    [InlineData("Opportunity for Independent Cargo Hauler | Patch City > Checkmate [100 Rep]",
                "opportunity for independent cargo hauler patch city checkmate")]
    public void NormalizeTitle_StripsRepTagAndPunctuation(string raw, string expected) =>
        Assert.Equal(expected, ContractParser.NormalizeTitle(raw));
}
