using NexusApp.Services;
using NexusApp.Services.Map;
using Xunit;

namespace NexusApp.Tests;

// PriceLocationLabel is the one place four surfaces (Codex dossier and cards, work order sell block,
// decoder scan card, overlay SCAN line) get their "where is this price" text. Four copies of a rule
// is four chances to disagree about it, so the rule lives here and so do its tests.
//
// The SILENCE RULE is the one worth defending: anything that does not resolve returns null, and the
// caller renders exactly what it rendered before the feature existed. Never a placeholder, never
// "unknown", never a zero. A price whose location cannot be established is still a good price.
public class PriceLocationLabelTests
{
    private static readonly MapCatalog Map = MapCatalog.LoadEmbedded();

    private static MarketTerminal Term(string system, string location)
        => new(7, $"T:{location}", "commodity", false, system, location);

    private static MapObject? At(string system, string name) => Map.ByName(system, name);

    [Fact]
    public void NoPlayerSession_ShowsTheSystemAlone()
    {
        // The half that needed no geometry and works the moment the terminal id exists. Most users
        // most of the time have no live session, so this is the common case, not the fallback.
        Assert.Equal("Stanton", PriceLocationLabel.Describe(Term("Stanton", "Everus Harbor"), Map, playerAt: null));
    }

    [Fact]
    public void PlayerInTheSameSystem_AppendsARealDistance()
    {
        var label = PriceLocationLabel.Describe(Term("Stanton", "Everus Harbor"), Map, At("Stanton", "microTech"));

        Assert.StartsWith("Stanton, ", label);
        Assert.EndsWith(" Gm", label);
    }

    [Fact]
    public void PlayerInAnotherSystem_SaysSoInWords_NeverABlank()
    {
        // DistanceMeters deliberately returns null across systems, because jump travel is not
        // Euclidean. Rendering that as a blank would read as "we failed to measure" rather than
        // "this is somewhere else entirely", so the cross-system case is spelled out.
        var label = PriceLocationLabel.Describe(Term("Stanton", "Everus Harbor"), Map, At("Pyro", "Ruin Station"));

        Assert.Equal("Stanton, another system", label);
    }

    [Fact]
    public void UnresolvableTerminal_FallsBackToTheSystem_NotToNothing()
    {
        // The terminal's system is known from UEX even when its location string matches no map
        // object, so the system half still stands on its own.
        var label = PriceLocationLabel.Describe(Term("Stanton", "Nowhere At All"), Map, At("Stanton", "microTech"));

        Assert.Equal("Stanton", label);
    }

    [Fact]
    public void NullTerminal_IsSilence()
        => Assert.Null(PriceLocationLabel.Describe((MarketTerminal?)null, Map, At("Stanton", "microTech")));

    [Fact]
    public void TerminalWithNoSystem_IsSilence()
    {
        // A terminal UEX gave us with a blank system cannot honestly be placed anywhere.
        Assert.Null(PriceLocationLabel.Describe(Term("", "Everus Harbor"), Map, playerAt: null));
    }

    // ── the id-based overload the WPF call sites actually use ──

    [Fact]
    public void IdOverload_ResolvesThroughTheSnapshot()
    {
        var terminals = new List<MarketTerminal> { Term("Stanton", "Everus Harbor") };
        Assert.Equal("Stanton", PriceLocationLabel.Describe(7, terminals, Map, playerAt: null));
    }

    [Fact]
    public void IdOverload_UnknownId_IsSilence()
    {
        var terminals = new List<MarketTerminal> { Term("Stanton", "Everus Harbor") };
        Assert.Null(PriceLocationLabel.Describe(999, terminals, Map, playerAt: null));
    }

    [Fact]
    public void IdOverload_ZeroId_IsSilence()
    {
        // 0 is what a PriceHit built before the id existed carries, and what hand-built test
        // fixtures get from the trailing default. It must never be treated as a real lookup.
        var terminals = new List<MarketTerminal> { Term("Stanton", "Everus Harbor") };
        Assert.Null(PriceLocationLabel.Describe(0, terminals, Map, playerAt: null));
    }

    [Fact]
    public void IdOverload_NoSnapshot_IsSilence()
        => Assert.Null(PriceLocationLabel.Describe(7, null, Map, playerAt: null));

    // ── DistanceOnly: the cramped-surface variant (overlay SCAN line) ──

    [Fact]
    public void DistanceOnly_SameSystem_IsABareFigure_NoSystemPrefix()
    {
        var terminals = new List<MarketTerminal> { Term("Stanton", "Everus Harbor") };
        var label = PriceLocationLabel.DistanceOnly(7, terminals, Map, At("Stanton", "microTech"));

        Assert.EndsWith(" Gm", label);
        Assert.DoesNotContain("Stanton", label);
    }

    [Fact]
    public void DistanceOnly_IsSilentWhereDescribeWouldStillSaySomething()
    {
        // The whole point of the second method. Describe falls back to a bare system name, which is
        // worth printing on the Codex and work order rows but would only crowd a line read at a
        // glance mid-flight. Here, anything short of a real number is nothing.
        var terminals = new List<MarketTerminal> { Term("Stanton", "Everus Harbor") };

        Assert.Null(PriceLocationLabel.DistanceOnly(7, terminals, Map, playerAt: null));                    // no session
        Assert.Null(PriceLocationLabel.DistanceOnly(7, terminals, Map, At("Pyro", "Ruin Station")));        // another system
        Assert.NotNull(PriceLocationLabel.Describe(7, terminals, Map, playerAt: null));                     // ...but Describe still speaks
    }

    [Fact]
    public void DistanceOnly_UnresolvableTerminal_IsSilent()
    {
        var terminals = new List<MarketTerminal> { Term("Stanton", "Nowhere At All") };
        Assert.Null(PriceLocationLabel.DistanceOnly(7, terminals, Map, At("Stanton", "microTech")));
    }
}
