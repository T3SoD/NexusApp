using System.Collections;
using System.Resources;
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// The Mission Guides catalog is the single source of truth for both surfaces.
// These tests enforce completeness in BOTH directions so a dropped-in PNG without
// a catalog entry (or an entry pointing at a missing file) fails the build, not the app.
public class GuideCatalogTests
{
    private static HashSet<string> EmbeddedGuideKeys()
    {
        var asm = typeof(GuideCatalog).Assembly;
        using var s = asm.GetManifestResourceStream(asm.GetName().Name + ".g.resources")!;
        using var r = new ResourceReader(s);
        return r.Cast<DictionaryEntry>().Select(e => (string)e.Key)
                .Where(k => k.StartsWith("assets/guides/")).ToHashSet();
    }

    private static string PackUriToKey(string packUri) =>
        Uri.UnescapeDataString(packUri.Replace("pack://application:,,,/", "")).ToLowerInvariant();

    [Fact]
    public void Has_six_entries_with_unique_ids()
    {
        Assert.Equal(6, GuideCatalog.All.Count);
        Assert.Equal(6, GuideCatalog.All.Select(g => g.Id).Distinct().Count());
    }

    [Fact]
    public void Categories_are_spec_order()
        => Assert.Equal(new[] { "Contested Zones", "Tactical Strike Groups", "General" }, GuideCatalog.Categories);

    [Fact]
    public void Every_catalog_entry_resolves_to_an_embedded_resource()
    {
        var keys = EmbeddedGuideKeys();
        foreach (var g in GuideCatalog.All) Assert.Contains(PackUriToKey(g.PackUri), keys);
    }

    [Fact]
    public void Every_embedded_guide_has_a_catalog_entry()
    {
        var catalogKeys = GuideCatalog.All.Select(g => PackUriToKey(g.PackUri)).ToHashSet();
        foreach (var k in EmbeddedGuideKeys()) Assert.Contains(k, catalogKeys);
    }

    [Fact]
    public void Native_dimensions_match_spec()
    {
        var orb = GuideCatalog.All.Single(g => g.Id == "orbituary");
        Assert.Equal((5496, 5296), (orb.NativeWidth, orb.NativeHeight));
    }

    [Fact]
    public void No_creator_names_in_catalog()
    {
        foreach (var g in GuideCatalog.All)
            foreach (var s in new[] { g.Id, g.Title, g.Category, g.PackUri })
                Assert.DoesNotContain("kraken", s, StringComparison.OrdinalIgnoreCase);
    }
}
