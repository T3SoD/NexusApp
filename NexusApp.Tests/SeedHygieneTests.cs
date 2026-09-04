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
        // "Antium Arms Maroon" left this list in the 4.10 refresh: CIG now ships that
        // blueprint in game data, so its presence is upstream truth, not the old hand-add.
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

    [Fact]
    public void MiningDataVersionIsAtLeast_1_3_1()
    {
        using var doc = SeedTestFixture.LoadSeed();
        var raw = doc.RootElement.GetProperty("miningDataVersion").GetString();
        Assert.False(string.IsNullOrWhiteSpace(raw));
        Assert.True(Version.TryParse(raw, out var version), $"not a version: {raw}");
        Assert.True(version >= new Version(1, 3, 1), $"version regressed: {raw}");
    }
}
