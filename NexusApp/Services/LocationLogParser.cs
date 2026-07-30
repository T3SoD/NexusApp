using System.Globalization;
using System.Text.RegularExpressions;

namespace NexusApp.Services;

// Pure parsing of Game.log location-timeline lines (recon: nexus-assets/specs/2026-07-28-
// gamelog-datacore-discovery.md, Tier 1 item 2 - "sparse but unambiguous"). Anchored on stable
// prefixes only, per the recon's own caution: the notification TEXT is localized game copy that
// can shift between patches, so only the tag plus the leading literal ("Entered ") is trusted,
// never the full sentence. No PII: RequestLocationInventory's/Update Inventory Location's
// Player[...] token is matched past and discarded, never captured.
public static class LocationLogParser
{
    public readonly record struct LocationSignal(string Place, DateTime SeenUtc);

    // <SHUDEvent_OnNotification> also carries Contract Accepted / New Objective / Received
    // Blueprint variants (HaulLogParser's and the blueprint session's territory already) - this
    // regex only matches the "Entered ..." jurisdiction/monitored-space family by requiring that
    // exact literal right after the opening quote.
    private static readonly Regex Jurisdiction = new(
        @"^<(?<ts>[0-9T:.Z+-]+)>.*?<SHUDEvent_OnNotification> Added notification " +
        @"""Entered (?<place>.+?)(?: Jurisdiction)?: """,
        RegexOptions.Compiled);

    // Real line: "<RequestLocationInventory> Player[TestPilot] requested inventory for
    // Location[Stanton4_NewBabbage]" - no space before "[TestPilot]".
    private static readonly Regex InventoryRequest = new(
        @"^<(?<ts>[0-9T:.Z+-]+)>.*?<RequestLocationInventory> Player\[[^\]]*\] requested inventory " +
        @"for Location\[(?<place>[^\]]+)\]",
        RegexOptions.Compiled);

    // Real line: "<Update Inventory Location> Player [TestPilot] is changing location." - WITH a
    // space before "[TestPilot]" (verified distinct from RequestLocationInventory's spacing).
    // Numeric landing/location ids follow; there is no name lookup table for them yet.
    private static readonly Regex InventoryTransition = new(
        @"^<(?<ts>[0-9T:.Z+-]+)>.*?<Update Inventory Location> Player \[[^\]]*\] is changing location\.",
        RegexOptions.Compiled);

    public static LocationSignal? ParseJurisdiction(string raw) => Match(Jurisdiction, raw);

    public static LocationSignal? ParseLocationInventory(string raw) => Match(InventoryRequest, raw);

    // Numeric-only transition: the ids have no name lookup yet (recon: "joinable to readable keys
    // over time" - deferred), so this reports freshness only, never a place name.
    public static DateTime? ParseInventoryTransitionUtc(string raw)
    {
        var m = InventoryTransition.Match(raw);
        return m.Success ? ParseTs(m.Groups["ts"].Value) : null;
    }

    private static LocationSignal? Match(Regex r, string raw)
    {
        var m = r.Match(raw);
        return m.Success ? new LocationSignal(m.Groups["place"].Value, ParseTs(m.Groups["ts"].Value)) : null;
    }

    private static DateTime ParseTs(string s) =>
        DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto)
            ? dto.UtcDateTime : DateTime.UtcNow;
}
