using System.Collections.Generic;

namespace NexusApp.Services;

/// <summary>
/// Pure composition seams for the overlay HUB tab's VITALS layout (2026-08-16 redesign, mock
/// overlay-operations O2). The three mini job cards and the session line are decisions, not
/// plumbing, so they live here and are tested; the WPF painting stays in OverlayWindow.
/// </summary>
public static class OverlayHub
{
    /// <summary>The session line: liveness and channel in one breath. The word carries the state
    /// as well as the LED beside it, because color must never be the only signal.</summary>
    public static string SessionLine(bool live, string? channel)
        => (live ? "LIVE" : "OFFLINE")
         + (string.IsNullOrWhiteSpace(channel) ? "" : $" / {channel}");

    /// <summary>REFINERY mini value: ready wins the headline, then refining, then CLEAR. RDY and
    /// REF because the card is 87px wide and the sub line below carries the long words.</summary>
    public static string RefineryValue(int ready, int refining)
        => ready > 0 ? $"{ready} RDY" : refining > 0 ? $"{refining} REF" : "CLEAR";

    /// <summary>REFINERY mini sub: the fact the headline did not use.</summary>
    public static string RefinerySub(int ready, int refining)
        => ready > 0 ? $"{refining} refining"
         : refining > 0 ? "none ready yet"
         : "no work orders";

    /// <summary>AUTO LOAD mini sub: load counts, never crate progress - the game logs no per-crate
    /// events, so a crates-done reading would be an invention. At most two facts fit 87px; finished
    /// yields to the states that still need the player only when something is still moving.</summary>
    public static string AutoLoadSub(int running, int untimed, int finished)
    {
        if (running == 0 && untimed == 0 && finished == 0) return "no load running";
        var parts = new List<string>(2);
        if (running > 0) parts.Add($"{running} running");
        if (untimed > 0 && parts.Count < 2) parts.Add($"{untimed} untimed");
        if (finished > 0 && parts.Count < 2) parts.Add($"{finished} done");
        return string.Join(", ", parts);
    }

    /// <summary>HANGAR mini sub: which way the door is moving plus the countdown.</summary>
    public static string HangarSub(bool isOpen, string countdown)
        => (isOpen ? "closes " : "opens ") + countdown;
}
