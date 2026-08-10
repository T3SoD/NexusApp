using NexusApp.Models;
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// The overlay CARGO tab badge (2026-08-10): finished auto-loads the player has not looked at yet.
// Derived from the entries, a watermark and the clock rather than counted by an event, because
// finishing is the passage of time and nothing arrives in the log to mark it.
public class AutoLoadBadgeTests
{
    private static readonly DateTime T0 = new(2026, 8, 10, 12, 0, 0, DateTimeKind.Utc);

    private static AutoLoadEntry Entry(DateTime startUtc, int? predictedSeconds) => new(
        startUtc, TransactionKind.Buy, "Everus Harbor", "Scrap", "Everus Harbor", false,
        680m, Array.Empty<CargoBoxGroup>(), predictedSeconds);

    [Fact]
    public void CompletesAt_IsTheStartPlusThePrediction()
        => Assert.Equal(T0.AddSeconds(300), AutoLoadBadge.CompletesAt(Entry(T0, 300)));

    // No prediction means no moment to call it finished. Guessing would put a number on the tab
    // for a load that may still be running.
    [Fact]
    public void CompletesAt_IsNullWithoutAPrediction()
        => Assert.Null(AutoLoadBadge.CompletesAt(Entry(T0, null)));

    [Fact]
    public void NothingLoading_CountsNothing()
        => Assert.Equal(0, AutoLoadBadge.CompletedSince(Array.Empty<AutoLoadEntry>(), T0, T0.AddHours(1)));

    [Fact]
    public void AFinishedLoad_Counts()
    {
        var entries = new[] { Entry(T0, 300) };
        Assert.Equal(1, AutoLoadBadge.CompletedSince(entries, T0.AddSeconds(-1), T0.AddSeconds(301)));
    }

    [Fact]
    public void AStillRunningLoad_DoesNotCount()
    {
        var entries = new[] { Entry(T0, 300) };
        Assert.Equal(0, AutoLoadBadge.CompletedSince(entries, T0.AddSeconds(-1), T0.AddSeconds(299)));
    }

    // Opening the CARGO tab moves the watermark to now, so everything already counted falls behind
    // it and the badge reads zero without any entry being touched or forgotten.
    [Fact]
    public void OpeningTheTab_ClearsWhatWasAlreadyCounted()
    {
        var entries = new[] { Entry(T0, 300) };
        var afterDone = T0.AddSeconds(301);
        Assert.Equal(1, AutoLoadBadge.CompletedSince(entries, T0.AddSeconds(-1), afterDone));
        Assert.Equal(0, AutoLoadBadge.CompletedSince(entries, afterDone, afterDone));   // watermark bumped
    }

    // ...and a load that finishes AFTER the tab was opened still counts, so the badge comes back.
    [Fact]
    public void ALoadFinishingAfterTheTabWasOpened_CountsAgain()
    {
        var opened = T0.AddSeconds(10);
        var entries = new[] { Entry(T0, 300) };
        Assert.Equal(1, AutoLoadBadge.CompletedSince(entries, opened, T0.AddSeconds(301)));
    }

    [Fact]
    public void AnUnpredictedLoad_NeverCounts()
    {
        var entries = new[] { Entry(T0, null) };
        Assert.Equal(0, AutoLoadBadge.CompletedSince(entries, T0.AddSeconds(-1), T0.AddHours(5)));
    }

    [Fact]
    public void SeveralFinished_AllCount()
    {
        var entries = new[] { Entry(T0, 60), Entry(T0, 120), Entry(T0, 9_000) };
        Assert.Equal(2, AutoLoadBadge.CompletedSince(entries, T0.AddSeconds(-1), T0.AddSeconds(200)));
    }

    // The ticker exists only to notice a countdown running out, so it must stop when nothing can.
    [Fact]
    public void AnyPending_TrueOnlyWhileSomethingIsStillLoading()
    {
        var entries = new[] { Entry(T0, 300) };
        Assert.True(AutoLoadBadge.AnyPending(entries, T0.AddSeconds(299)));
        Assert.False(AutoLoadBadge.AnyPending(entries, T0.AddSeconds(301)));
        Assert.False(AutoLoadBadge.AnyPending(Array.Empty<AutoLoadEntry>(), T0));
    }

    [Fact]
    public void AnyPending_IgnoresUnpredictedLoads()
        => Assert.False(AutoLoadBadge.AnyPending(new[] { Entry(T0, null) }, T0));

    // ---- overlay wiring pins ------------------------------------------------------------------

    // The TRADE badge is gone. Accepted routes are standing state you can read by opening the tab,
    // not news; the count sat there permanently and never asked for anything.
    [Fact]
    public void Overlay_NoLongerBadgesTheTradeTabWithAcceptedRoutes()
    {
        var src = SourceFiles.ReadAppSource(@"Views\OverlayWindow.xaml.cs");
        Assert.DoesNotContain("SetBadge(\"trade\"", src);
    }

    [Fact]
    public void Overlay_BadgesCargoWithFinishedAutoLoads()
    {
        var src = SourceFiles.ReadAppSource(@"Views\OverlayWindow.xaml.cs");
        Assert.Contains("AutoLoadBadge.CompletedSince", src);
        Assert.Contains("SetBadge(\"hauling\", count)", src);
    }

    [Fact]
    public void Overlay_ClearsTheCargoBadgeWhenThatTabIsOpened()
    {
        var src = SourceFiles.ReadAppSource(@"Views\OverlayWindow.xaml.cs");
        Assert.Contains("if (tab == \"hauling\") _cargoBadgeSince = DateTime.UtcNow;", src);
    }

    // The ticker is gated so no timer runs for the window's whole life for something that happens
    // a few times an hour.
    [Fact]
    public void Overlay_RunsTheBadgeTickerOnlyWhileSomethingIsLoading()
    {
        var src = SourceFiles.ReadAppSource(@"Views\OverlayWindow.xaml.cs");
        Assert.Contains("AutoLoadBadge.AnyPending", src);
        Assert.Contains("_cargoBadgeTimer.Stop();", src);
    }

    // A star reads as a favourite; accepting a route is taking on work. Both surfaces now use the
    // same verb and the same gold.
    [Fact]
    public void Overlay_AcceptsRoutesWithAWordNotAStar()
    {
        var src = SourceFiles.ReadAppSource(@"Views\OverlayWindow.xaml.cs");
        Assert.Contains("accepted ? \"ACCEPTED\" : \"ACCEPT\"", src);
        Assert.DoesNotContain("PlannerPinStarGeometry", src);
    }

    // A route you accepted an hour ago is useless if you cannot see where to BUY without opening
    // the desktop app. The desktop card has always named both legs.
    [Fact]
    public void Overlay_AcceptedRouteCard_NamesBothLegs()
    {
        var src = SourceFiles.ReadAppSource(@"Views\OverlayWindow.xaml.cs");
        Assert.Contains("$\"{route.BuyTerminalName} -> {route.SellTerminalName}\"", src);
    }

    [Fact]
    public void Desktop_AcceptedRouteCard_NamesBothLegsToo()
    {
        var src = SourceFiles.ReadAppSource(@"Views\HaulingPage.cs");
        Assert.Contains("$\"{r.BuyTerminalName} -> {r.SellTerminalName}\"", src);
    }
}
