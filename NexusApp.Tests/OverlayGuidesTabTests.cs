using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// Headless coverage for the overlay's saved-tab restore guard. GUIDES is the sixth tab, so a
// saved "guides" has to come back as "guides" instead of falling through to the default, and
// every other id must still round-trip. Unknown or legacy values fall back rather than leaving
// the overlay on no tab at all.
public class OverlayGuidesTabTests
{
    [Theory]
    [InlineData("stats", "stats")]
    [InlineData("scan", "scan")]
    [InlineData("orders", "orders")]
    [InlineData("shopping", "shopping")]
    [InlineData("hauling", "hauling")]
    [InlineData("guides", "guides")]
    [InlineData("", "stats")]
    [InlineData(null, "stats")]
    [InlineData("legacy-junk", "stats")]
    [InlineData("GUIDES", "stats")]     // ids are case-sensitive keys; unknown casing falls back
    public void NormalizeForRestore_AcceptsGuidesAndGuardsUnknown(string? saved, string expected)
        => Assert.Equal(expected, OverlayTabs.NormalizeForRestore(saved));

    [Fact]
    public void Ids_AreTheSixTabs()
        => Assert.Equal(new[] { "stats", "scan", "orders", "shopping", "hauling", "guides" }, OverlayTabs.Ids);

    [Fact]
    public void Every_id_restores_to_itself()
    {
        foreach (var id in OverlayTabs.Ids)
            Assert.Equal(id, OverlayTabs.NormalizeForRestore(id));
    }
}
