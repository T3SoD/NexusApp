using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// Fixtures are REAL OCR text captured from the in-game Contract Manager (nexus.log), not idealized
// screenshots: there is no "Reward"/"Contracted By" label on screen, the reward sits before the "N/A"
// deadline, and the contractor org follows it.
public class ContractParserTests
{
    private const string Cfp =
        "PRIMARY OBJECTIVES x Ä 139,250 N/A Citizens For Prosperity [50/200 Rep] Need a Hauler DETAILS " +
        "Hi, we need to move some cargo. O Deliver 0/11 SCU of Hydrogen to Ruin Station above Pyro VI. " +
        "o Collect Hydrogen from Jackson's Swap. TRACK";

    private const string Ling =
        "PRIMARY OBJECTIVES x Ä 169,250 N/A Ling Family Hauling 169k " +
        "Deliver 0/4 SCU of Sunset Berries to Rayari Cantwell Research Outpost on Clio. " +
        "o Collect Sunset Berries from Chawla's Beach. " +
        "Deliver 0/2 SCU of Golden Medmon to Rayari Cantwell Research Outpost on Clio. " +
        "o Collect Golden Medmon from Chawla's Beach. UNTRACK";

    private const string Covalex =
        "x Ä 119,000 N/A Covalex Independent Contractors 119k 169k PRIMARY OBJECTIVES " +
        "O Deliver 0/6 SCU of Medical Supplies to Patch City at the L3 Lagrange of Pyro III. " +
        "o Collect Medical Supplies from Checkmate. TRACK";

    [Fact]
    public void Parse_Cfp_RewardContractorAndCargo()
    {
        var d = ContractParser.Parse(Cfp);
        Assert.NotNull(d);
        Assert.Equal(139250, d!.Reward);
        Assert.Equal("Citizens For Prosperity", d.ContractedBy);
        var o = Assert.Single(d.Objectives);
        Assert.Equal("Hydrogen", o.Commodity);
        Assert.Equal(11, o.Scu);
        Assert.Equal("Jackson's Swap", o.Pickup);
        Assert.StartsWith("Ruin Station", o.Dropoff);
    }

    [Fact]
    public void Parse_Ling_TwoObjectivesAndContractor()
    {
        var d = ContractParser.Parse(Ling);
        Assert.Equal(169250, d!.Reward);
        Assert.Equal("Ling Family Hauling", d.ContractedBy);
        Assert.Equal(2, d.Objectives.Count);
        Assert.Contains(d.Objectives, o => o.Commodity == "Sunset Berries" && o.Scu == 4 && o.Pickup == "Chawla's Beach");
        Assert.Contains(d.Objectives, o => o.Commodity == "Golden Medmon" && o.Scu == 2);
    }

    [Fact]
    public void Parse_Covalex_RewardBeforeNa()
    {
        var d = ContractParser.Parse(Covalex);
        Assert.Equal(119000, d!.Reward);
        Assert.Equal("Covalex Independent Contractors", d.ContractedBy);
        var o = Assert.Single(d.Objectives);
        Assert.Equal("Medical Supplies", o.Commodity);
        Assert.Equal(6, o.Scu);
        Assert.Equal("Checkmate", o.Pickup);
    }

    [Fact]
    public void Parse_DotSeparatorReward_AndContractorStopsBeforeDeliver()
    {
        var d = ContractParser.Parse(
            "x Ä 169.250 N/A Ling Family Hauling Deliver 0/4 SCU of Sunset Berries to X. o Collect Sunset Berries from Y.");
        Assert.Equal(169250, d!.Reward);
        Assert.Equal("Ling Family Hauling", d.ContractedBy);
    }

    [Fact]
    public void Parse_StripsOcrBulletLettersFromContractor()
    {
        // Real OCR puts the objective checkbox bullets ("O O") right after the org with no separator.
        var d = ContractParser.Parse(
            "PRIMARY OBJECTIVES x Ä 169.250 N/A Ling Family Hauling O O " +
            "Deliver 0/4 SCU of Sunset Berries to Rayari. o Collect Sunset Berries from Chawla's Beach.");
        Assert.Equal("Ling Family Hauling", d!.ContractedBy);
    }

    [Fact]
    public void Parse_DropoffStopsBeforeCollect_NoOverCapture()
    {
        // Real OCR: a misread period after the dropoff left "Sunset Mesa on o Collect ..." with no '.'.
        var d = ContractParser.Parse(
            "x Ä 104,500 N/A Red Wind Linehaul Deliver 0/22 SCU of Hydrogen to Sunset Mesa on o Collect Hydrogen from Jackson's Swap.");
        var o = Assert.Single(d!.Objectives);
        Assert.Equal("Hydrogen", o.Commodity);
        Assert.DoesNotContain("Collect", o.Dropoff);
        Assert.Equal("Jackson's Swap", o.Pickup);
    }

    [Fact]
    public void Parse_ScreenChromeOnly_ReturnsNull()
    {
        Assert.Null(ContractParser.Parse("OFFERS ACCEPTED (0/10) HISTORY BEACONS Please select a contract."));
    }
}
