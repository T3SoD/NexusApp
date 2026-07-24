using System.Text.Json;
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// Guards the owner-approved seed-data hygiene pass (feature/seed-data-hygiene):
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
        "Antium Arms Maroon",
    };

    private static JsonDocument LoadSeed()
    {
        using var stream = typeof(DataService).Assembly
            .GetManifestResourceStream("NexusApp.Data.seed_data.json");
        Assert.NotNull(stream);
        using var ms = new MemoryStream();
        stream!.CopyTo(ms);
        return JsonDocument.Parse(ms.ToArray());
    }

    private static IEnumerable<JsonElement> Blueprints(JsonDocument doc) =>
        doc.RootElement.GetProperty("blueprints").EnumerateArray();

    private static IEnumerable<JsonElement> BlueprintUnlocks(JsonDocument doc) =>
        doc.RootElement.GetProperty("blueprintUnlocks").EnumerateArray();

    [Fact]
    public void NoUnlockUsesPlaceholderFaction()
    {
        using var doc = LoadSeed();
        foreach (var u in BlueprintUnlocks(doc))
        {
            var faction = u.TryGetProperty("faction", out var f) ? f.GetString() : null;
            Assert.NotEqual("<= PLACEHOLDER =>", faction);
        }
    }

    [Fact]
    public void BlueprintNamesAreUnique()
    {
        using var doc = LoadSeed();
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
        using var doc = LoadSeed();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var b in Blueprints(doc))
        {
            var name = b.GetProperty("name").GetString();
            if (name != null) names.Add(name);
        }
        foreach (var deleted in DeletedBlueprintNames)
            Assert.False(names.Contains(deleted), $"deleted blueprint still present: {deleted}");
    }

    // The 7 missions removed or restructured in SC 4.9 whose 72 unlock rows were deleted.
    // Mission titles are stable identifiers, unlike row counts, which change on every
    // legitimate seed refresh, so this locks the deletion without churning on future data.
    private static readonly string[] DeletedMissionTitles =
    {
        "URGENT FLEET REFUEL",
        "Knock Out New Drug Op",
        "Yellow Level Contract: Ship Under Attack",
        "Destroy Dangerous Drugs",
        "Destroy Illegal Drugs",
        "Red Level Contract: Ship Under Attack",
        "Orange Level Contract: [SHIP] Needs Assistance",
    };

    [Fact]
    public void NoUnlockReferencesARemovedMission()
    {
        using var doc = LoadSeed();
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
        using var doc = LoadSeed();
        var raw = doc.RootElement.GetProperty("miningDataVersion").GetString();
        Assert.False(string.IsNullOrWhiteSpace(raw));
        Assert.True(Version.TryParse(raw, out var version), $"not a version: {raw}");
        Assert.True(version >= new Version(1, 3, 1), $"version regressed: {raw}");
    }
}
