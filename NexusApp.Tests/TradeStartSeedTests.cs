using NexusApp.Views;
using Xunit;

namespace NexusApp.Tests;

// TradePage.SeedStartKind (A5, final review F4): the Starting Location combo's first-render seed,
// extracted pure from RefreshStartCombo. The bug it pins: opening the planner before the first
// market fetch lands handed the seed an EMPTY terminal list, so a saved terminal name looked
// stale and fell open to LIVE for the rest of the session. A null return means "defer" - stay
// unseeded and let the next refresh, with a real list, make the call.
public class TradeStartSeedTests
{
    private static readonly string[] Names = ["Admin - Port Olisar", "TDD - Trade and Development Division - Orison"];

    [Fact]
    public void SavedTerminalName_EmptyList_Defers()
        // THE A5 repro: consent on, no cached snapshot, a saved start location. The empty list
        // says nothing about the name's validity, so the seed must wait, not guess.
        => Assert.Null(TradePage.SeedStartKind("Admin - Port Olisar", []));

    [Fact]
    public void SavedTerminalName_PresentInList_Seeds()
        => Assert.Equal("Admin - Port Olisar", TradePage.SeedStartKind("Admin - Port Olisar", Names));

    [Fact]
    public void SavedTerminalName_MissingFromRealList_FailsOpenToLive()
        // A non-empty list that lacks the name means the name genuinely went stale (terminal
        // renamed or gone) - that is the case the old fail-open was FOR, and it stays.
        => Assert.Equal("LIVE", TradePage.SeedStartKind("Old Renamed Terminal", Names));

    [Theory]
    [InlineData("ANY")]
    [InlineData("LIVE")]
    public void AnyAndLive_SeedImmediately_EvenOnEmptyList(string kind)
        // Neither kind names a terminal, so neither has any reason to wait for the list.
        => Assert.Equal(kind, TradePage.SeedStartKind(kind, []));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void FirstRun_DefaultsToLive_EvenOnEmptyList(string? persisted)
        // Nothing persisted = first run. The LIVE default (old FROM HERE) needs no data either.
        => Assert.Equal("LIVE", TradePage.SeedStartKind(persisted, []));
}
