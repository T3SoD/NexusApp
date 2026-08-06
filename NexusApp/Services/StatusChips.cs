using System;
using NexusApp.ViewModels;

namespace NexusApp.Services;

/// <summary>
/// Pure composition seams for the header status strip (F14 pill consolidation, 2026-08-01).
/// The strip went from five chips of mixed importance to multi-feature lamps: GAME SESSION
/// (absorbing the SHARD chip as its value suffix), UEX (the MARKET chip, renamed to match the
/// Trade page's pill for the same feed), and AUTO-SCAN (one chip for BOTH OCR scanners, an
/// amendment on the mock). The LOCATION readout born in the same pass lives in the dock
/// foot operator panel now (issue #40). These seams are the parts of that rework that are
/// decisions rather
/// than plumbing, so they are pure and tested; the WPF painting stays in MainWindow.
/// </summary>
public static class StatusChips
{
    /// <summary>The old SHARD chip's display text, now the session chip's suffix: "US-E · 042",
    /// plus a channel tag only when it is something other than LIVE. Null when nothing usable is
    /// detected - the session chip renders silence rather than "not detected", because the shard
    /// is session metadata now and an offline session already says everything.</summary>
    public static string? ShardText(string? region, string? instance, string? channel)
    {
        if (string.IsNullOrWhiteSpace(region)) return null;
        var text = string.IsNullOrWhiteSpace(instance) ? region : $"{region} · {instance}";
        if (!string.IsNullOrWhiteSpace(channel) && channel != "LIVE") text += $" · {channel}";
        return text;
    }

    /// <summary>The merged session chip's value text. Live carries only FACTS - the shard and the
    /// channel suffix - because the breathing green dot and the SESSION label already say "alive";
    /// the word "monitoring" restated them and was cut (2026-08-01). With nothing detected the
    /// live value is empty, which is correct silence, not a gap. The shard rides only the live
    /// state: with the session offline the last-seen shard is history, not status, and appending
    /// it would dress a dead reading as a current one.</summary>
    public static string SessionValue(bool live, string channelSuffix, string? shard)
    {
        if (!live) return "offline" + channelSuffix;
        var value = (shard ?? "") + channelSuffix;
        // A bare channel suffix (" · PTU") would otherwise lead with its separator.
        return value.StartsWith(" · ", StringComparison.Ordinal) ? value[3..] : value;
    }

    /// <summary>The contract scanner's three-state read, extracted verbatim from the overlay's
    /// SyncHaulingControls so the header AUTO-SCAN chip and the overlay HUB LED can never derive
    /// it differently: running = on; enabled-but-not-running = paused (foreground-gated); else
    /// off.</summary>
    public static ScanIndicator ContractScanState(bool running, bool enabled)
        => running ? ScanIndicator.On : enabled ? ScanIndicator.Paused : ScanIndicator.Off;

    /// <summary>One LED for two scanners (amended on the mock: the AUTO-SCAN chip must represent
    /// both OCR auto-scan features). Paused outranks On because paused is the state worth a
    /// glance - the scanners pause THEMSELVES on foreground rules, and a green lamp while either
    /// is silently paused would be the lamp lying. On outranks Off because something genuinely
    /// running deserves the live green even when its sibling is switched off by choice.</summary>
    public static ScanIndicator AutoScanCombined(ScanIndicator rs, ScanIndicator contracts)
        => rs == ScanIndicator.Paused || contracts == ScanIndicator.Paused ? ScanIndicator.Paused
         : rs == ScanIndicator.On || contracts == ScanIndicator.On ? ScanIndicator.On
         : ScanIndicator.Off;

    /// <summary>The chip's value text spells out both scanners so the aggregate dot never hides
    /// which one is in which state: "RS on · CT off".</summary>
    public static string AutoScanText(ScanIndicator rs, ScanIndicator contracts)
        => $"RS {Word(rs)} · CT {Word(contracts)}";

    private static string Word(ScanIndicator s) => s switch
    {
        ScanIndicator.On => "on",
        ScanIndicator.Paused => "paused",
        _ => "off",
    };

    /// <summary>The location lamps' shared fold (2026-08-04: the pills wore the live cyan
    /// forever, game running or not - they must go red when no session is live). Used by the dock
    /// LOCATION chip and the Trade page's ORIGIN chip. Offline outranks coarse: with the game
    /// closed the reading is history regardless of its precision, and red is the lamp saying
    /// tracking is NOT current. LastKnownLocation is app-lifetime (seeded from the previous
    /// session's Game.log at startup and never cleared), so location-known alone can never mean
    /// live; sessionLive is the game-process probe (App.GameLogFeed.IsSessionLive - the pure
    /// probe, not GameLogSession's attachment-gated read, which goes false when the log monitor
    /// is stopped while the game still runs).</summary>
    public static LocationLamp LocationLampState(bool locationKnown, bool coarse, bool sessionLive)
        => !locationKnown ? LocationLamp.Unknown
         : !sessionLive ? LocationLamp.Offline
         : coarse ? LocationLamp.Coarse
         : LocationLamp.Live;
}

/// <summary>What a location readout may claim right now: Unknown (dim, no reading), Live (cyan,
/// precise place with a live session behind it), Coarse (dim, jurisdiction-only reading), or
/// Offline (red, a reading with no live session behind it).</summary>
public enum LocationLamp { Unknown, Live, Coarse, Offline }
