using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// The overlay HUB VITALS seams (2026-08-16 redesign). Every string on the mini cards is a
// decision about what fits 87px and what the app can honestly claim; each rule is pinned here.
public class OverlayHubTests
{
    [Theory]
    [InlineData(true, "LIVE", "LIVE / LIVE")]         // channel LIVE reads "LIVE / LIVE" by design
    [InlineData(true, "PTU", "LIVE / PTU")]
    [InlineData(false, "LIVE", "OFFLINE / LIVE")]
    [InlineData(true, null, "LIVE")]
    [InlineData(false, "", "OFFLINE")]
    public void SessionLine_CarriesStateInWords(bool live, string? channel, string expected)
        => Assert.Equal(expected, OverlayHub.SessionLine(live, channel));

    [Theory]
    [InlineData(2, 4, "2 RDY")]     // ready wins the headline
    [InlineData(0, 4, "4 REF")]
    [InlineData(0, 0, "CLEAR")]
    public void RefineryValue_ReadyWins(int ready, int refining, string expected)
        => Assert.Equal(expected, OverlayHub.RefineryValue(ready, refining));

    [Theory]
    [InlineData(2, 4, "4 refining")]        // the headline said ready, the sub says the rest
    [InlineData(2, 0, "0 refining")]
    [InlineData(0, 4, "none ready yet")]
    [InlineData(0, 0, "no work orders")]
    public void RefinerySub_CarriesTheOtherFact(int ready, int refining, string expected)
        => Assert.Equal(expected, OverlayHub.RefinerySub(ready, refining));

    [Theory]
    [InlineData(0, 0, 0, "no load running")]
    [InlineData(1, 0, 0, "1 running")]
    [InlineData(2, 1, 0, "2 running, 1 untimed")]
    [InlineData(1, 0, 1, "1 running, 1 done")]
    [InlineData(1, 1, 1, "1 running, 1 untimed")]   // two facts max; done yields to moving states
    [InlineData(0, 0, 2, "2 done")]
    public void AutoLoadSub_TwoFactsMax(int running, int untimed, int finished, string expected)
        => Assert.Equal(expected, OverlayHub.AutoLoadSub(running, untimed, finished));

    [Theory]
    [InlineData(true, "41:12", "closes 41:12")]
    [InlineData(false, "12:04", "opens 12:04")]
    public void HangarSub_SaysWhichWayTheDoorMoves(bool open, string cd, string expected)
        => Assert.Equal(expected, OverlayHub.HangarSub(open, cd));
}
