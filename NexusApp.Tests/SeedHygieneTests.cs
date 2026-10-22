using System.Text.Json;
using Xunit;

namespace NexusApp.Tests;

// Guards the seed-data hygiene pass (feature/seed-data-hygiene):
// the removed racing/drug/refuel/placeholder rows must stay gone, blueprint names
// must stay unique, and the content version must not regress below 1.3.1.
// Reads the embedded seed the same way DataService does at runtime.
public class SeedHygieneTests
{
    private static readonly string[] DeletedBlueprintNames =
    {
        "BlackFire Racing Flight Suit",
        "BlackFire Racing Helmet",
        "BlueFlame Racing Flight Suit",
        "BlueFlame Racing Helmet",
        "Mirai Racing Flight Suit",
        "Mirai Racing Helmet",
        "WhiteHot Racing Flight Suit",
        "WhiteHot Racing Helmet",
        // "Antium Arms Maroon" left this list in the 4.10 refresh: CIG ships that blueprint
        // in game data. The 4.10.2 localization swapped its name with the Jet helmet while
        // the records stayed put, so the seed keeps the 4.10.1 names for those two items.
    };

    private static IEnumerable<JsonElement> Blueprints(JsonDocument doc) =>
        doc.RootElement.GetProperty("blueprints").EnumerateArray();

    private static IEnumerable<JsonElement> BlueprintUnlocks(JsonDocument doc) =>
        doc.RootElement.GetProperty("blueprintUnlocks").EnumerateArray();

    [Fact]
    public void NoUnlockUsesPlaceholderFaction()
    {
        using var doc = SeedTestFixture.LoadSeed();
        foreach (var u in BlueprintUnlocks(doc))
        {
            var faction = u.TryGetProperty("faction", out var f) ? f.GetString() : null;
            Assert.NotEqual("<= PLACEHOLDER =>", faction);
        }
    }

    [Fact]
    public void BlueprintNamesAreUnique()
    {
        using var doc = SeedTestFixture.LoadSeed();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var b in Blueprints(doc))
        {
            var name = b.GetProperty("name").GetString();
            Assert.NotNull(name);
            Assert.True(seen.Add(name!), $"duplicate blueprint name: {name}");
        }
    }

    [Fact]
    public void DeletedBlueprintsAreAbsent()
    {
        using var doc = SeedTestFixture.LoadSeed();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var b in Blueprints(doc))
        {
            var name = b.GetProperty("name").GetString();
            if (name != null) names.Add(name);
        }
        foreach (var deleted in DeletedBlueprintNames)
            Assert.False(names.Contains(deleted), $"deleted blueprint still present: {deleted}");
    }

    // Missions removed or restructured in SC 4.9 whose unlock rows were deleted.
    // Mission titles are stable identifiers, unlike row counts, which change on every
    // legitimate seed refresh, so this locks the deletion without churning on future data.
    // 4.10 reintroduced three of the original seven ("Yellow/Red Level Contract: Ship
    // Under Attack", "Orange Level Contract: [SHIP] Needs Assistance") as live Foxwell
    // contracts, so those left the list with the 4.10 refresh.
    private static readonly string[] DeletedMissionTitles =
    {
        "URGENT FLEET REFUEL",
        "Knock Out New Drug Op",
        "Destroy Dangerous Drugs",
        "Destroy Illegal Drugs",
    };

    [Fact]
    public void NoUnlockReferencesARemovedMission()
    {
        using var doc = SeedTestFixture.LoadSeed();
        var deleted = new HashSet<string>(DeletedMissionTitles, StringComparer.Ordinal);
        foreach (var u in BlueprintUnlocks(doc))
        {
            var title = u.TryGetProperty("missionTitle", out var t) ? t.GetString() : null;
            if (title != null)
                Assert.False(deleted.Contains(title), $"unlock references removed mission: {title}");
        }
    }

    // The 14 recipes the compare's category filter silently dropped until data 1.4.1 (the
    // Metamaterial Test #152 field report): typeless mission items and vehicle gear. Pinned
    // so a rebuilt seed cannot quietly lose them again.
    [Fact]
    public void RecoveredTypelessRecipes_ArePresent()
    {
        using var doc = SeedTestFixture.LoadSeed();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var b in Blueprints(doc))
            if (b.GetProperty("name").GetString() is { } n) names.Add(n);
        foreach (var required in new[]
        {
            "Metamaterial Test #146", "Metamaterial Test #152", "Probe", "TH-01 Propulsor",
            "Argo Ore Pod", "Drake Ore Pod", "GOLEM MC-4 Ore Pod", "MISC Ore Pod",
            "MISC Enhanced Ore Pod", "Clearcut Module", "Deluge Module", "Overrun Module",
            "Stampede Module", "Torrent III Module",
        })
            Assert.True(names.Contains(required), $"recovered recipe missing from seed: {required}");
    }

    [Fact]
    public void MiningDataVersionIsAtLeast_1_3_1()
    {
        using var doc = SeedTestFixture.LoadSeed();
        var raw = doc.RootElement.GetProperty("miningDataVersion").GetString();
        Assert.False(string.IsNullOrWhiteSpace(raw));
        Assert.True(Version.TryParse(raw, out var version), $"not a version: {raw}");
        Assert.True(version >= new Version(1, 3, 1), $"version regressed: {raw}");
    }

    // Crafting quality forecast pins (data 1.5.0): slots, craft stats, quality bands, starters.
    private static IEnumerable<(string Blueprint, JsonElement Slot)> Slots(JsonDocument doc)
    {
        foreach (var b in Blueprints(doc))
        {
            if (!b.TryGetProperty("slots", out var slots)) continue;
            var name = b.GetProperty("name").GetString() ?? "";
            foreach (var s in slots.EnumerateArray()) yield return (name, s);
        }
    }

    private static readonly string[] MisspelledResources = { "Aluminum", "Hephaestanite", "Quantainium" };

    [Fact]
    public void EveryRangeHasExactlyOnePair()
    {
        using var doc = SeedTestFixture.LoadSeed();
        foreach (var (bp, slot) in Slots(doc))
        {
            if (!slot.TryGetProperty("modifiers", out var mods)) continue;
            foreach (var mod in mods.EnumerateArray())
            {
                var stat = mod.GetProperty("stat").GetString();
                foreach (var r in mod.GetProperty("ranges").EnumerateArray())
                {
                    foreach (var prop in r.EnumerateObject())
                        Assert.True(prop.Value.ValueKind != JsonValueKind.Null,
                            $"{bp} / {stat}: range property {prop.Name} is null");
                    bool mul = r.TryGetProperty("m0", out _) && r.TryGetProperty("m1", out _);
                    bool add = r.TryGetProperty("a0", out _) && r.TryGetProperty("a1", out _);
                    bool anyMul = r.TryGetProperty("m0", out _) || r.TryGetProperty("m1", out _);
                    bool anyAdd = r.TryGetProperty("a0", out _) || r.TryGetProperty("a1", out _);
                    Assert.True(mul ^ add, $"{bp} / {stat}: range must carry exactly one of m0+m1 or a0+a1");
                    Assert.False(anyMul && anyAdd, $"{bp} / {stat}: range mixes multiplicative and additive properties");
                }
            }
        }
    }

    [Fact]
    public void EveryStatKeyExistsInCraftStats()
    {
        using var doc = SeedTestFixture.LoadSeed();
        var stats = doc.RootElement.GetProperty("craftStats");
        foreach (var (bp, slot) in Slots(doc))
        {
            if (!slot.TryGetProperty("modifiers", out var mods)) continue;
            foreach (var mod in mods.EnumerateArray())
            {
                var stat = mod.GetProperty("stat").GetString() ?? "";
                Assert.True(stats.TryGetProperty(stat, out _), $"{bp}: stat key not in craftStats: {stat}");
            }
        }
    }

    [Fact]
    public void SlotAndBandResourcesUseAppSpelling()
    {
        using var doc = SeedTestFixture.LoadSeed();
        foreach (var (bp, slot) in Slots(doc))
            foreach (var res in slot.GetProperty("resources").EnumerateArray())
                Assert.False(MisspelledResources.Contains(res.GetString()),
                    $"{bp}: slot resource uses non-app spelling: {res.GetString()}");
        foreach (var band in doc.RootElement.GetProperty("qualityBands").EnumerateObject())
            Assert.False(MisspelledResources.Contains(band.Name),
                $"qualityBands key uses non-app spelling: {band.Name}");
    }

    [Fact]
    public void EverySlotFirstResourceIsAnIngredientUnlessItem()
    {
        using var doc = SeedTestFixture.LoadSeed();
        foreach (var b in Blueprints(doc))
        {
            if (!b.TryGetProperty("slots", out var slots)) continue;
            var name = b.GetProperty("name").GetString() ?? "";
            var ingredients = new HashSet<string>(StringComparer.Ordinal);
            foreach (var i in b.GetProperty("ingredients").EnumerateArray())
                if (i.GetProperty("resourceName").GetString() is { } r) ingredients.Add(r);
            foreach (var slot in slots.EnumerateArray())
            {
                if (slot.TryGetProperty("item", out var item) && item.GetBoolean()) continue;
                var slotName = slot.GetProperty("name").GetString();
                var resources = slot.GetProperty("resources");
                Assert.True(resources.GetArrayLength() > 0, $"{name} / {slotName}: non-item slot has no resources");
                var first = resources[0].GetString() ?? "";
                Assert.True(ingredients.Contains(first),
                    $"{name} / {slotName}: first resource {first} is not an ingredient");
            }
        }
    }

    [Fact]
    public void MiningDataVersionIsAtLeast_1_5_0()
    {
        using var doc = SeedTestFixture.LoadSeed();
        var raw = doc.RootElement.GetProperty("miningDataVersion").GetString();
        Assert.False(string.IsNullOrWhiteSpace(raw));
        Assert.True(Version.TryParse(raw, out var version), $"not a version: {raw}");
        Assert.True(version >= new Version(1, 5, 0), $"version regressed: {raw}");
    }

    [Fact]
    public void StarterBlueprintsExistAndIncludeP4AR()
    {
        using var doc = SeedTestFixture.LoadSeed();
        var starters = doc.RootElement.GetProperty("starterBlueprints").EnumerateArray()
            .Select(e => e.GetString() ?? "").ToList();
        Assert.NotEmpty(starters);
        Assert.Contains(starters, n => n.Contains("P4-AR", StringComparison.Ordinal));
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var b in Blueprints(doc))
            if (b.GetProperty("name").GetString() is { } n) names.Add(n);
        foreach (var s in starters)
            Assert.True(names.Contains(s), $"starter blueprint not in blueprints: {s}");
    }
}
