using System;
using System.Collections.Generic;
using NexusApp.Models;
using NexusApp.Services.Map;
using Xunit;

namespace NexusApp.Tests;

// App review G11: every map layer was static reference data - the same pins for every player, every
// session. These two are built from THIS pilot's own state, which is what turns the map from a
// reference chart into something that answers "where do I have to go".
public class MapLayersLiveStateTests
{
    private static readonly MapCatalog Map = MapCatalog.LoadEmbedded();

    // "Everus Harbor" is an exact catalog object name, so it exercises the resolver's direct-hit
    // tier without depending on an alias that could be retuned later.
    private const string RealPlace = "Everus Harbor";
    private const string SecondPlace = "Port Tressler";

    private static Haul HaulWith(params HaulLeg[] legs)
    {
        var h = new Haul { MissionId = Guid.NewGuid().ToString(), PickupName = RealPlace };
        foreach (var l in legs) h.Legs.Add(l);
        return h;
    }

    private static HaulLeg Drop(string destination, string commodity = "Titanium", bool completed = false) =>
        new() { Role = HaulRole.Dropoff, Destination = destination, Commodity = commodity, Completed = completed };

    // ---- BuildHauls ---------------------------------------------------------------------------

    [Fact]
    public void BuildHauls_PinsADropoffAtItsDestination()
    {
        var pins = MapLayers.BuildHauls(new[] { HaulWith(Drop(SecondPlace)) }, Map);

        var obj = Map.ByName("Stanton", SecondPlace)!;
        Assert.True(pins.ContainsKey(obj.Id));
        Assert.Contains("Drop Titanium", pins[obj.Id]);
    }

    [Fact]
    public void BuildHauls_PinsThePickupToo_BecauseAMapCaresWhereYouMustGo()
    {
        var pins = MapLayers.BuildHauls(new[] { HaulWith(new HaulLeg { Role = HaulRole.Pickup }) }, Map);

        var obj = Map.ByName("Stanton", RealPlace)!;
        Assert.True(pins.ContainsKey(obj.Id));
        Assert.Contains("Pickup", pins[obj.Id]);
    }

    [Fact]
    public void BuildHauls_SkipsCompletedLegs()
    {
        var pins = MapLayers.BuildHauls(new[] { HaulWith(Drop(SecondPlace, completed: true)) }, Map);
        Assert.False(pins.ContainsKey(Map.ByName("Stanton", SecondPlace)!.Id));
    }

    [Fact]
    public void BuildHauls_SkipsFinishedContracts()
    {
        var haul = HaulWith(Drop(SecondPlace));
        haul.Outcome = HaulOutcome.Complete;

        Assert.Empty(MapLayers.BuildHauls(new[] { haul }, Map));
    }

    [Fact]
    public void BuildHauls_UnresolvablePlaceIsSilentlyAbsent_NotPinnedSomewherePlausible()
    {
        // Stop names are OCR or Game.log free text. "Pickup (TBD)" is a real one, and a map that
        // guessed at a place for it would be worse than one that says nothing.
        var pins = MapLayers.BuildHauls(new[] { HaulWith(Drop("Pickup (TBD)")) }, Map);
        Assert.Empty(pins);
    }

    [Fact]
    public void BuildHauls_TwoContractsToOnePlace_ShareOneEntryWithBothLabels()
    {
        var pins = MapLayers.BuildHauls(
            new[] { HaulWith(Drop(SecondPlace, "Titanium")), HaulWith(Drop(SecondPlace, "Laranite")) }, Map);

        var labels = pins[Map.ByName("Stanton", SecondPlace)!.Id];
        Assert.Equal(2, labels.Count);
        Assert.Contains("Drop Titanium", labels);
        Assert.Contains("Drop Laranite", labels);
    }

    [Fact]
    public void BuildHauls_DuplicateLabelAtOnePlace_IsNotRepeated()
    {
        var pins = MapLayers.BuildHauls(
            new[] { HaulWith(Drop(SecondPlace, "Titanium")), HaulWith(Drop(SecondPlace, "Titanium")) }, Map);

        Assert.Single(pins[Map.ByName("Stanton", SecondPlace)!.Id]);
    }

    // The hard contract every layer provider shares: a key only ever exists once a value was pushed
    // onto it. MapSceneBuilder derives its per-object booleans from key presence alone, so an
    // empty-list entry would flag an object as pinned with nothing to show.
    [Fact]
    public void BuildHauls_NeverProducesAnEmptyList()
    {
        var pins = MapLayers.BuildHauls(new[] { HaulWith(Drop(SecondPlace), Drop("Nowhere Real")) }, Map);
        Assert.All(pins.Values, list => Assert.NotEmpty(list));
    }

    [Fact]
    public void BuildHauls_NoHauls_IsEmpty() => Assert.Empty(MapLayers.BuildHauls(Array.Empty<Haul>(), Map));

    // ---- BuildOrders --------------------------------------------------------------------------

    private static WorkOrder Order(string refinery, string label = "Batch 1",
                                   WorkOrderStatus status = WorkOrderStatus.Refining) =>
        new() { Refinery = refinery, Label = label, Status = status };

    [Fact]
    public void BuildOrders_PinsARunningOrderAtItsRefinery()
    {
        var pins = MapLayers.BuildOrders(new[] { Order("Ruin Station") }, Map);

        var obj = Map.ByName("Pyro", "Ruin Station")!;
        Assert.True(pins.ContainsKey(obj.Id));
        Assert.Contains("Batch 1", pins[obj.Id]);
    }

    [Fact]
    public void BuildOrders_StripsTheUexParenthetical_AndFindsTheRightGateway()
    {
        // A refinery picked from the work order editor is spelled in UEX vocabulary. The
        // parenthetical names the system it SITS in, so stripping it and searching by base name
        // lands on the gateway itself rather than on the star the parenthetical mentions.
        var pins = MapLayers.BuildOrders(new[] { Order("Stanton Gateway (Pyro)") }, Map);

        var obj = Map.ByName("Pyro", "Stanton Gateway")!;
        Assert.True(pins.ContainsKey(obj.Id));
    }

    [Fact]
    public void BuildOrders_SkipsCompletedOrders()
        => Assert.Empty(MapLayers.BuildOrders(new[] { Order("Ruin Station", status: WorkOrderStatus.Complete) }, Map));

    [Fact]
    public void BuildOrders_OrderWithNoRefinery_IsSkipped()
        => Assert.Empty(MapLayers.BuildOrders(new[] { Order("") }, Map));

    [Fact]
    public void BuildOrders_UnknownRefineryName_IsSilentlyAbsent()
        => Assert.Empty(MapLayers.BuildOrders(new[] { Order("Somewhere That Does Not Exist") }, Map));

    [Fact]
    public void BuildOrders_UnlabelledOrder_FallsBackToItsStatus_RatherThanAnEmptyPin()
    {
        var pins = MapLayers.BuildOrders(new[] { Order("Ruin Station", label: "") }, Map);
        Assert.Contains("Refining", pins[Map.ByName("Pyro", "Ruin Station")!.Id]);
    }

    [Fact]
    public void BuildOrders_NeverProducesAnEmptyList()
    {
        var pins = MapLayers.BuildOrders(new[] { Order("Ruin Station"), Order("Nowhere") }, Map);
        Assert.All(pins.Values, list => Assert.NotEmpty(list));
    }

    [Fact]
    public void BuildOrders_NoOrders_IsEmpty()
        => Assert.Empty(MapLayers.BuildOrders(Array.Empty<WorkOrder>(), Map));

    // ---- The scene payload --------------------------------------------------------------------

    [Fact]
    public void BuildInit_CarriesTheTwoNewLayerFlagsAndPerObjectBooleans()
    {
        var obj = Map.ByName("Stanton", SecondPlace)!;
        var pins = new MapLayerPins(
            new Dictionary<int, IReadOnlyList<int>>(),
            new Dictionary<int, string>(),
            new Dictionary<int, IReadOnlyList<string>>(),
            null,
            new Dictionary<int, IReadOnlyList<string>> { [obj.Id] = new[] { "Drop Titanium" } },
            new Dictionary<int, IReadOnlyList<string>>());

        var json = MapSceneBuilder.BuildInit(Map, "Stanton", pins,
            tradeOn: false, guidesOn: false, miningOn: false, hangarOn: false, asteroidsOn: false,
            selection: null, draft: Array.Empty<int>(), planner: Array.Empty<int>(), reduced: true,
            player: null, haulsOn: true, ordersOn: false);

        Assert.Contains("\"hauls\":true", json);
        Assert.Contains("\"orders\":false", json);
        Assert.Contains("\"haul\":true", json);
    }

    // Every construction site that predates G11 - including the ones in this repo's own fixtures -
    // must keep working and must produce exactly the scene it produced before.
    [Fact]
    public void MapLayerPins_BuiltWithoutTheLiveLayers_ReportsThemEmpty()
    {
        var pins = new MapLayerPins(
            new Dictionary<int, IReadOnlyList<int>>(),
            new Dictionary<int, string>(),
            new Dictionary<int, IReadOnlyList<string>>(),
            null);

        Assert.Empty(pins.Hauls);
        Assert.Empty(pins.Orders);
    }
}
