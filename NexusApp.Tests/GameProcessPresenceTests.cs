using System;
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// The SC-exit breadcrumb decision: one "[FG] StarCitizen process exited" line per game
// lifetime, derived handle-free from foreground changes plus a process-list existence probe
// (never a handle to the game process, by explicit product decision: no anti-cheat risk).
public class GameProcessPresenceTests
{
    [Fact]
    public void NeverSeenInForeground_NeverReportsExit()
    {
        var p = new GameProcessPresence();
        Assert.False(p.Update(gameIsForeground: false, gameProcessExists: () => false));
        Assert.False(p.Update(gameIsForeground: false, gameProcessExists: () => false));
    }

    [Fact]
    public void SeenThenBackgroundedWhileStillRunning_DoesNotReport()
    {
        var p = new GameProcessPresence();
        Assert.False(p.Update(true, () => true));
        Assert.False(p.Update(false, () => true));
    }

    [Fact]
    public void SeenThenGone_ReportsExactlyOnce()
    {
        var p = new GameProcessPresence();
        p.Update(true, () => true);
        Assert.True(p.Update(false, () => false));
        Assert.False(p.Update(false, () => false));
    }

    [Fact]
    public void RelaunchedGame_RearmsAndReportsAgain()
    {
        var p = new GameProcessPresence();
        p.Update(true, () => true);
        Assert.True(p.Update(false, () => false));
        p.Update(true, () => true);
        Assert.True(p.Update(false, () => false));
    }

    [Fact]
    public void WhileGameIsForeground_ExistenceIsNotProbed()
    {
        // The probe snapshots the process list; skip it entirely while the game itself holds
        // foreground (it is self-evidently running).
        var p = new GameProcessPresence();
        Assert.False(p.Update(true, () => throw new InvalidOperationException("must not probe")));
    }
}
