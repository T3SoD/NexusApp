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

    private void Apply(string place, DateTime seenUtc, string kind)
    {
        LastKnownLocation = place;
        LastSeenUtc = seenUtc;
        Logger.Info($"[WHERE] location updated: {place} (source: {kind})");
        Changed?.Invoke();
    }

    public void Dispose()
    {
        _sub?.Dispose();
        _sub = null;
        if (_ownsFeed) _feed.Dispose();
    }
}
