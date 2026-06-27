using NexusApp.Models;
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// Real (redacted) current-build (12061511) Game.log lines as fixtures, inline per repo convention.
public class HaulLogParserTests
{
    [Fact]
    public void ParseMarker_Pickup_ExtractsAllFields()
    {
        var m = HaulLogParser.ParseMarker(HaulLogParserFixtures.MarkerPickup);
        Assert.NotNull(m);
        Assert.Equal("a6c598e0-e8d2-4613-a7a3-e80f4c30d9c6", m!.MissionId);
        Assert.Equal("RedWind_Hauling", m.Generator);
        Assert.Equal(HaulRole.Pickup, m.Role);
        Assert.Equal("pickup_6e9335e8-f62e-4eca-8fc5-a42ab1c3ab7d_0", m.ObjectiveId);
        Assert.Equal("6e9335e8-f62e-4eca-8fc5-a42ab1c3ab7d_0", m.CargoKey);
        Assert.Equal(0, m.LegIndex);
        Assert.Equal(2281204.528538, m.X, 3);
    }

    [Fact]
    public void ParseMarker_Dropoff_RoleIsDropoff()
    {
        var m = HaulLogParser.ParseMarker(HaulLogParserFixtures.MarkerDropoff);
        Assert.Equal(HaulRole.Dropoff, m!.Role);
        Assert.Equal("6e9335e8-f62e-4eca-8fc5-a42ab1c3ab7d_0", m.CargoKey);
    }

    [Fact]
    public void ParseMarker_NonHaulContract_ReturnsNull()
    {
        var bounty = HaulLogParserFixtures.MarkerPickup.Replace(
            "contract [RedWind_Pyro_SupplyGrade_RegionA_CFP_StationToTradepost_Carbon_CargoHauling_AtoB_Intro]",
            "contract [BountyHuntersGuild_Bounty_Stanton_Medium_2]");
        Assert.Null(HaulLogParser.ParseMarker(bounty));
    }

    [Fact]
    public void ParseDeliver_ExtractsCommodityScuDestination()
    {
        var d = HaulLogParser.ParseDeliver(HaulLogParserFixtures.DeliverLine);
        Assert.NotNull(d);
        Assert.Equal("a6c598e0-e8d2-4613-a7a3-e80f4c30d9c6", d!.MissionId);
        Assert.Equal("dropoff_6e9335e8-f62e-4eca-8fc5-a42ab1c3ab7d_0", d.ObjectiveId);
        Assert.Equal("Carbon", d.Commodity);
        Assert.Equal(158, d.TargetScu);
        Assert.Equal("Jackson's Swap", d.Destination);
    }

    [Fact]
    public void ParseDeliver_NonScuObjective_ReturnsNull()
    {
        var dataObjective = HaulLogParserFixtures.DeliverLine.Replace("Deliver 0/158 SCU of Carbon to Jackson's Swap",
                                                "Deliver Energy Anomaly Data to Port Tressler's Freight Elevator");
        Assert.Null(HaulLogParser.ParseDeliver(dataObjective));
    }

    [Fact]
    public void ParseContractAccepted_StripsEmTags_AndKeepsRoute()
    {
        var a = HaulLogParser.ParseContractAccepted(HaulLogParserFixtures.AcceptRouteLine);
        Assert.NotNull(a);
        Assert.Equal("a6c598e0-e8d2-4613-a7a3-e80f4c30d9c6", a!.MissionId);
        Assert.DoesNotContain("<EM", a.Title);
        Assert.Contains("Ruin Station > Baijini Point", a.Title);
    }

    [Fact]
    public void ParseObjectiveCompleted_OnlyCompletedState()
    {
        var c = HaulLogParser.ParseObjectiveCompleted(HaulLogParserFixtures.ObjectiveCompletedLine);
        Assert.NotNull(c);
        Assert.Equal("pickup_6e9335e8-f62e-4eca-8fc5-a42ab1c3ab7d_0", c!.ObjectiveId);
        Assert.Null(HaulLogParser.ParseObjectiveCompleted(HaulLogParserFixtures.ObjectiveInProgressLine));
    }

    [Fact]
    public void ParseEndMission_MapsCompletionType_NoPii()
    {
        var done = HaulLogParser.ParseEndMission(HaulLogParserFixtures.EndComplete);
        Assert.Equal("6ea3467c-83c8-4b41-b25c-ead2c558cc54", done!.MissionId);
        Assert.Equal(HaulOutcome.Complete, done.Outcome);
        Assert.Equal(HaulOutcome.Abandoned, HaulLogParser.ParseEndMission(HaulLogParserFixtures.EndAbandon)!.Outcome);
    }

    [Theory]
    [InlineData("HaulCargo_AToB_NonMetal_Carbon_Stanton2_SmallGrade", "1 to 1")]
    [InlineData("RedWind_Pyro_..._CargoHauling_AtoB_Intro", "1 to 1")]
    [InlineData("HaulCargo_SingleToMulti2_Processed_AgriculturalSupplies_Stanton2_SmallGrade", "1 to 2")]
    [InlineData("HaulCargo_SingleToMulti3_Processed_Stims_Stanton2_SmallGrade1", "1 to 3")]
    [InlineData("HaulCargo_Multi2ToSingle_Waste_Waste_Stanton1_SupplyGrade", "2 to 1")]
    [InlineData("Something_Unrecognized", "Unknown")]
    public void ParseTopology_FromContractName(string contract, string expected) =>
        Assert.Equal(expected, HaulLogParser.ParseTopology(contract));

    [Theory]
    [InlineData("RedWind_Hauling", "Red Wind")]
    [InlineData("Covalex_Hauling", "Covalex")]
    public void CompanyDisplay_KnownGenerators(string generator, string expected) =>
        Assert.Equal(expected, HaulLogParser.CompanyDisplay(generator));
}
