using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// RoutePlanner.ChosenSystemOutsideScope backs the planner's A3 empty-state rungs. The scope pill
// and the START/DESTINATION pickers are independent controls that can be pointed at two different
// systems; when that happens every leg is filtered away and the planner used to report it as
// "No routes match the current scope and budget", which is indistinguishable from a genuine dry
// spell. This is the check that lets the empty state name the contradiction instead.
public class RoutePlannerScopeConflictTests
{
    private static MarketTerminal Term(int id, string system) =>
        new(id, $"Terminal {id}", "commodity", false, system, "");

    private static readonly Dictionary<int, MarketTerminal> Terminals = new()
    {
        [1] = Term(1, "Stanton"),
        [2] = Term(2, "Pyro"),
        [3] = Term(3, "Pyro"),
        [4] = Term(4, ""),        // no recorded system
    };

    [Fact]
    public void PyroDestinationUnderStantonScope_NamesTheSystemItIsActuallyIn()
    {
        // The exact repro: scope STANTON, destination Checkmate Station (Pyro). Zero routes, and
        // until now no hint that the two settings contradict each other.
        Assert.Equal("Pyro", RoutePlanner.ChosenSystemOutsideScope(new HashSet<int> { 2 }, Terminals, "Stanton"));
    }

    [Fact]
    public void StantonDestinationUnderPyroScope_IsTheSameBugInReverse()
    {
        Assert.Equal("Stanton", RoutePlanner.ChosenSystemOutsideScope(new HashSet<int> { 1 }, Terminals, "Pyro"));
    }

    [Fact]
    public void ChoiceInsideTheScope_IsNoConflict()
    {
        Assert.Null(RoutePlanner.ChosenSystemOutsideScope(new HashSet<int> { 1 }, Terminals, "Stanton"));
    }

    [Fact]
    public void OneReachableTerminalAmongSeveral_IsEnoughToClearTheConflict()
    {
        // A picked location can map to several terminals. If even one is reachable the planner has
        // something real to search, so this must not fire - the user would be told to fix a
        // contradiction that is not stopping anything.
        Assert.Null(RoutePlanner.ChosenSystemOutsideScope(new HashSet<int> { 1, 2 }, Terminals, "Stanton"));
    }

    [Fact]
    public void ScopeAll_CanNeverConflict()
    {
        Assert.Null(RoutePlanner.ChosenSystemOutsideScope(new HashSet<int> { 2 }, Terminals, "ALL"));
        Assert.Null(RoutePlanner.ChosenSystemOutsideScope(new HashSet<int> { 2 }, Terminals, "all"));
    }

    [Fact]
    public void EmptyScopeString_CanNeverConflict()
    {
        // Matches InScope: an empty scope means no filtering at all.
        Assert.Null(RoutePlanner.ChosenSystemOutsideScope(new HashSet<int> { 2 }, Terminals, ""));
    }

    [Fact]
    public void NoConstraint_IsNotAConflict()
    {
        // null = the picker is on ANY. Nothing was chosen, so nothing can contradict the scope.
        Assert.Null(RoutePlanner.ChosenSystemOutsideScope(null, Terminals, "Stanton"));
    }

    [Fact]
    public void UnresolvedPick_IsNotAConflict()
    {
        // An empty (non-null) set means the picker's name resolved to no terminal at all. That is
        // a different failure with its own message ("Starting location unknown"), and reporting it
        // as a scope conflict would send the user to the wrong control.
        Assert.Null(RoutePlanner.ChosenSystemOutsideScope(new HashSet<int>(), Terminals, "Stanton"));
    }

    [Fact]
    public void TerminalWithNoRecordedSystem_FallsBackToTheGenericMessage()
    {
        // InScope treats a blank system as out of scope, but there is no system to name here, so
        // claiming one would be a fabrication. Null lets the generic empty-state message stand.
        Assert.Null(RoutePlanner.ChosenSystemOutsideScope(new HashSet<int> { 4 }, Terminals, "Stanton"));
    }

    [Fact]
    public void UnknownTerminalIds_AreSkipped_NotTreatedAsInScope()
    {
        // An id with no row in the terminals dictionary is unresolvable, not reachable. Skipping it
        // leaves the real Pyro terminal to answer, so the conflict is still reported.
        Assert.Equal("Pyro", RoutePlanner.ChosenSystemOutsideScope(new HashSet<int> { 99, 2 }, Terminals, "Stanton"));
    }

    [Fact]
    public void ScopeMatchIsCaseInsensitive_MatchingInScope()
    {
        Assert.Null(RoutePlanner.ChosenSystemOutsideScope(new HashSet<int> { 1 }, Terminals, "stanton"));
    }

    [Fact]
    public void RankReallyDoesReturnNothingInTheConflictCase()
    {
        // Ties the helper to the behaviour it explains: with a Pyro-only destination and a Stanton
        // scope there is genuinely a complete, profitable route in the data, and Rank still returns
        // nothing. Without this the helper could drift into describing a state that never happens.
        var rows = new List<TradePriceRow>
        {
            new(1, 47, 100, 0, 500, 0, 1, 0, "1,2,4,8,16,24,32", DateTime.UtcNow, "Terminal 1", "Commodity 47"),
            new(2, 47, 0, 200, 0, 500, 0, 1, "1,2,4,8,16,24,32", DateTime.UtcNow, "Terminal 2", "Commodity 47"),
        };

        var acrossBoth = RoutePlanner.Rank(rows, Terminals, shipScu: 100, shipMaxBox: 32, budget: null,
            originTerminalIds: null, scope: "ALL", take: 10, destTerminalIds: new HashSet<int> { 2 });
        Assert.Single(acrossBoth);   // the route exists

        var scopedOut = RoutePlanner.Rank(rows, Terminals, shipScu: 100, shipMaxBox: 32, budget: null,
            originTerminalIds: null, scope: "Stanton", take: 10, destTerminalIds: new HashSet<int> { 2 });
        Assert.Empty(scopedOut);     // and the scope silently removes it

        Assert.Equal("Pyro", RoutePlanner.ChosenSystemOutsideScope(new HashSet<int> { 2 }, Terminals, "Stanton"));
    }
}
