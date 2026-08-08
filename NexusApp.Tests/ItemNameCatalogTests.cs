using System;
using System.IO;
using System.Linq;
using System.Text;
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// GUID first, token second. Both maps exist because CIG's entity tokens and localization keys
// disagree for some items, and because either identifier can drift between patches.
public class ItemNameCatalogTests
{
    private static ItemNameCatalog From(string json) =>
        ItemNameCatalog.Load(new MemoryStream(Encoding.UTF8.GetBytes(json)));

    private const string Sample = """
        {"guids":{
          "d4408421-e939-4e34-9902-0644fa6934be":"'Chaos' III Missile"
        },
        "names":{
          "crlf_consumable_healing_01":"MedPen (Hemozal)",
          "Carryable_1H_CY_medical_canister_healing_1":"C-71 Medical Case",
          "at_symbol":"@mp_ePistol",
          "placeholder_item":"PLACEHOLDER - placeholder_item"
        }}
        """;

    [Fact]
    public void Resolve_PrefersTheGuid()
    {
        Assert.Equal("'Chaos' III Missile",
            From(Sample).Resolve("d4408421-e939-4e34-9902-0644fa6934be", "unknown_token"));
    }

    // A known token alone cannot prove the GUID is checked first, only that the token was
    // consulted and missed. This pins precedence under a real conflict: same item, both maps
    // populated, different values, GUID must win.
    [Fact]
    public void Resolve_GuidWinsWhenBothMapsKnowTheItem()
    {
        var json = """
            {"guids":{"d4408421-e939-4e34-9902-0644fa6934be":"Name From Guid"},
             "names":{"MISL_S03_IR_VNCL_Chaos":"Name From Token"}}
            """;
        Assert.Equal("Name From Guid",
            From(json).Resolve("d4408421-e939-4e34-9902-0644fa6934be", "MISL_S03_IR_VNCL_Chaos"));
    }

    [Fact]
    public void Resolve_FallsBackToTheTokenWhenTheGuidMisses()
    {
        Assert.Equal("MedPen (Hemozal)",
            From(Sample).Resolve("00000000-0000-0000-0000-000000000000", "crlf_consumable_healing_01"));
        Assert.Equal("C-71 Medical Case",
            From(Sample).Resolve(null, "Carryable_1H_CY_medical_canister_healing_1"));
    }

    [Fact]
    public void Resolve_IsCaseInsensitiveOnBothKeys()
    {
        var c = From(Sample);
        Assert.Equal("'Chaos' III Missile", c.Resolve("D4408421-E939-4E34-9902-0644FA6934BE", null));
        Assert.Equal("MedPen (Hemozal)", c.Resolve(null, "CRLF_CONSUMABLE_HEALING_01"));
    }

    [Fact]
    public void Resolve_ReturnsNullWhenBothMiss()
    {
        var c = From(Sample);
        Assert.Null(c.Resolve("nope", "also_nope"));
        Assert.Null(c.Resolve(null, null));
        Assert.Null(c.Resolve("", ""));
    }

    [Fact]
    public void Resolve_RejectsNonNames()
    {
        var c = From(Sample);
        Assert.Null(c.Resolve(null, "at_symbol"));          // "@mp_ePistol" is a loc pointer
        Assert.Null(c.Resolve(null, "placeholder_item"));   // "PLACEHOLDER - ..." is not a name
    }

    // Finding 4: "<-=MISSING=->" is CIG's own missing-entry marker, 18 rows in the shipped table
    // (e.g. DebugGun), and rendered as an item name before this fix.
    [Fact]
    public void Resolve_RejectsTheMissingMarker()
    {
        var json = """{"guids":{},"names":{"DebugGun":"<-=MISSING=->"}}""";
        Assert.Null(From(json).Resolve(null, "DebugGun"));
    }

    [Fact]
    public void LoadEmbedded_RejectsTheMissingMarkerOnARealShippedRow()
    {
        var c = ItemNameCatalog.LoadEmbedded();
        Assert.Null(c.Resolve(null, "DebugGun"));
    }

    [Fact]
    public void Load_ReportsCount()
    {
        Assert.Equal(4, From(Sample).Count);
        Assert.Equal(0, From("""{"guids":{},"names":{}}""").Count);
    }

    // The shipped table, loaded the way the app loads it. Guards the csproj registration and the
    // resource name, and pins the four GUIDs captured live on 2026-08-07.
    [Fact]
    public void LoadEmbedded_ResolvesTheLiveCapturedItems()
    {
        var c = ItemNameCatalog.LoadEmbedded();
        Assert.True(c.Count > 8000);
        Assert.Equal("'Chaos' III Missile", c.Resolve("d4408421-e939-4e34-9902-0644fa6934be", null));
        Assert.Equal("APX Fire Extinguisher", c.Resolve("1b6a6b76-f3fc-402c-a24a-204f2eeae6f7", null));
        Assert.Equal("Agure", c.Resolve("94e97499-375e-4c63-a2ea-c605a4d3f461", null));
        Assert.Equal("CorticoPen (Sterogen)", c.Resolve("354ec8a6-32eb-4747-8e75-03d2703edfd6", null));
    }

    // Finding 3: ResolvePurchaseName is the wallet's shared entry point (ProfitTracker.Ingest at
    // apply time, and the WalletDisplay.PurchaseTitle fallback for a purchase whose name was never
    // populated). Same live-captured item as LoadEmbedded_ResolvesTheLiveCapturedItems above.
    [Fact]
    public void ResolvePurchaseName_ResolvesAKnownGuid()
    {
        Assert.Equal("'Chaos' III Missile",
            ItemNameCatalog.ResolvePurchaseName("d4408421-e939-4e34-9902-0644fa6934be", "MISL_S03_IR_VNCL_Chaos"));
    }

    // Finding 3(b): the coverage-report log must fire once per distinct token, not once per call
    // (the MarketQueries.LogMissOnce / UnmatchedBlueprintLog per-run dedupe idiom), or a session
    // full of purchases of one unrecognized item would flood nexus.log. A Guid-suffixed token keeps
    // this test independent of whatever else has run in the shared, parallel test process.
    [Fact]
    public void ResolvePurchaseName_LogsUnresolvedOnceOnlyPerToken()
    {
        var token = $"ZzzUnitTestUnresolved_{Guid.NewGuid():N}";

        Assert.Null(ItemNameCatalog.ResolvePurchaseName(null, token));
        Assert.Null(ItemNameCatalog.ResolvePurchaseName(null, token));
        Assert.Null(ItemNameCatalog.ResolvePurchaseName(null, token));

        var logPath = Environment.GetEnvironmentVariable("NEXUS_LOG_PATH");
        Assert.NotNull(logPath);
        var occurrences = TestFiles.ReadSharedLines(logPath!)
            .Count(l => l.Contains($"[WALLET] purchase name unresolved: {token}"));
        Assert.Equal(1, occurrences);
    }

    [Fact]
    public void ResolvePurchaseName_ReturnsNullWhenBothKeysMiss()
    {
        Assert.Null(ItemNameCatalog.ResolvePurchaseName(null, null));
        Assert.Null(ItemNameCatalog.ResolvePurchaseName("", ""));
    }

    [Fact]
    public void Resolve_NamesAVehicleBoughtThroughTheShoppingProvider()
    {
        var catalog = ItemNameCatalog.LoadEmbedded();
        // The GUID Game.log printed for a live Dragonfly purchase on 2026-08-08. Vehicles live in
        // entities/spaceships and entities/groundvehicles, not entities/scitem, so this only
        // resolves once the extractor walks them too.
        Assert.Equal("Drake Dragonfly",
            catalog.Resolve("37659ff0-a803-4a4f-97ff-ad59822061ed", "DRAK_Dragonfly"));
        Assert.Equal("Greycat STV",
            catalog.Resolve("d4662193-10ab-4912-8ca4-d64ead0e6f3b", "GRIN_STV"));
    }
}
