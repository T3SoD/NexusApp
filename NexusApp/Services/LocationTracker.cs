namespace NexusApp.Services;

// App-lifetime "last known location," derived from Game.log boundary-crossing lines (trading tab:
// the planner/sell-lookup live-origin default). Sparse and boundary-only by nature (recon: only
// SHUDEvent_OnNotification jurisdiction/monitored-space text and RequestLocationInventory fire,
// and only on crossings/arrivals/inventory opens) - this is a TIMELINE, not live position tracking.
// Consumers must label it "auto-detected" with an age (same as the SHARD chip) and fall back to
// manual entry when stale or absent; that UI decision belongs to the trading tab task, not here.
// No PII: only in-game location keys are read - a player's RSI handle is matched past and never
// captured (LocationLogParser).
//
// Deliberate deviation from ShardTracker's cold-replay-suppression idiom: ShardTracker suppresses
// its OnShard boolean during a cold replay because "on a shard RIGHT NOW" would otherwise be a
// false live claim sourced from history. LocationTracker has no such claim to protect -
// LastSeenUtc is always the LOG LINE'S OWN timestamp (never DateTime.UtcNow at ingest time), so a
// cold replay's last signal already reports its true age instead of lying "just now." No
// _staleReplay flag is needed for that reason; includeReplay:true is kept so a mid-session app
// restart still rebuilds today's last-known location from the current Game.log instead of
// starting blank.
public sealed class LocationTracker : IDisposable
{
    private readonly GameLogFeed _feed;
    private readonly bool _ownsFeed;
    private GameLogSubscription? _sub;

    public LocationTracker(GameLogFeed? feed = null)
    {
        _feed = feed ?? new GameLogFeed();
        _ownsFeed = feed is null;
        _sub = _feed.Subscribe(Ingest, includeReplay: true);
    }

    public string? LastKnownLocation { get; private set; }

    // The UEX Location string for LastKnownLocation, when the raw token that produced it is one
    // of the (currently gateway-only) tokens LocationAliases.UexLocationForToken recognizes - null
    // for every other place, including the common case where LastKnownLocation is itself already
    // a UEX-shaped Location string. LastKnownLocation cannot be reverse-mapped to a UEX Location
    // safely: display names are not unique (three raw gateway tokens all display as "Stanton
    // Gateway Station"), so this is resolved here, once, from the raw token Apply already has in
    // scope, rather than asking a consumer to guess a UEX Location from the display name later.
    // Consumers (TradeOriginResolver.TerminalIdsForLocation) treat this as a first-pass hint and
    // fall back to their existing display-name matching when it is null.
    public string? LastKnownUexLocation { get; private set; }

    // The raw Game.log token that produced LastKnownLocation (e.g. "RR_JP_StantonPyro"), retained
    // alongside the normalized display name for the same reason LastKnownUexLocation is: some
    // display names are not unique (three raw gateway tokens all display as "Stanton Gateway
    // Station"), so a consumer that needs to disambiguate - the MAP tab's player marker, resolving
    // through MapCatalog.ResolvePlayerLocation's raw-token gateway tier - reads this instead of
    // guessing from LastKnownLocation alone. Null whenever LastKnownLocation itself is null (no
    // signal ingested yet).
    public string? LastKnownRawToken { get; private set; }

    // True when LastKnownLocation came from a JURISDICTION crossing rather than a real place
    // signal (owner's live pass, 2026-08-01: the header LOCATION chip read "Crusader Industries"
    // at Crusader - a jurisdiction names whose SPACE you are in, not where you are). Stored so
    // display surfaces can say so instead of dressing an area up as a location; the value itself
    // still stands, since a coarse reading beats none.
    public bool LastKnownIsJurisdiction { get; private set; }

    public DateTime? LastSeenUtc { get; private set; }
    public event Action? Changed;

    public void Ingest(GameLogEntry e)
    {
        var raw = e.Raw;

        if (raw.Contains("<RequestLocationInventory>"))
        {
            if (LocationLogParser.ParseLocationInventory(raw) is { } sig)
                Apply(sig.Place, sig.SeenUtc, "inventory key");
            return;
        }
        if (raw.Contains("<SHUDEvent_OnNotification>"))
        {
            if (LocationLogParser.ParseJurisdiction(raw) is { } sig)
                Apply(sig.Place, sig.SeenUtc, "jurisdiction");
            return;
        }
        if (raw.Contains("<Update Inventory Location>"))
        {
            // Numeric landing/location ids only - no name lookup table exists yet (recon:
            // "joinable to readable keys over time," deferred). Counts as location ACTIVITY
            // (freshness) without overwriting whatever readable text a jurisdiction/inventory-key
            // line already set.
            if (LocationLogParser.ParseInventoryTransitionUtc(raw) is { } seenUtc)
            {
                LastSeenUtc = seenUtc;
                Changed?.Invoke();
            }
        }
    }

    // Freshness always advances; only a REAL transition is news. Twenty inventory opens at the same
    // station are twenty matching lines, and every launch replays the whole Game.log on top of that
    // (includeReplay: true above) - logging and raising on each would bury the App Log Monitor and
    // fan a rebuild out to every subscriber for a place that never changed. ShardTracker, the
    // sibling on this same feed, already logs and raises on transitions only.
    //
    // Normalized through LocationAliases BEFORE the change comparison and stored as the display
    // name: this is the single choke point, so the pill, OriginLabel, and any resolver reading
    // LastKnownLocation all inherit readable in-game names (e.g. "Stanton4_NewBabbage" ->
    // "New Babbage"), and repeated raw variants of the same normalized place do not re-fire
    // Changed. A miss (jurisdiction names like "microTech" are not in the table) passes through
    // unchanged, matching prior behavior exactly.
    private void Apply(string place, DateTime seenUtc, string kind)
    {
        var display = LocationAliases.Normalize(place);
        bool moved = !string.Equals(LastKnownLocation, display, StringComparison.Ordinal);
        LastKnownLocation = display;
        LastKnownUexLocation = LocationAliases.UexLocationForToken(place);
        LastKnownRawToken = place;
        LastKnownIsJurisdiction = kind == "jurisdiction";
        LastSeenUtc = seenUtc;
        if (!moved) return;
        var suffix = string.Equals(display, place, StringComparison.Ordinal)
            ? $"(source: {kind})"
            : $"(raw {place}, source: {kind})";
        Logger.Info($"[WHERE] location updated: {display} {suffix}");
        Changed?.Invoke();
    }

    public void Dispose()
    {
        _sub?.Dispose();
        _sub = null;
        if (_ownsFeed) _feed.Dispose();
    }
}
