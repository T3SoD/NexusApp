namespace NexusApp.Services;

/// <summary>
/// Watches the Game.log for kiosk box-size lines and files them against wherever the player was
/// standing (2026-08-10). Mirrors AutoLoadTracker's shape: a thin subscriber over the shared feed
/// that owns no UI and no WPF.
///
/// <para>The kiosk names a shopId, but nothing in this app maps a shopId to a UEX terminal and
/// shopName is a template shared across stations, so the player's LOCATION is what makes the
/// observation addressable. That is read at ingest time, not stored raw from the line, because the
/// line itself carries no place at all.</para>
/// </summary>
public sealed class KioskBoxTracker : IDisposable
{
    private readonly IDisposable? _sub;
    private readonly KioskBoxSizeStore _store;
    private readonly Func<(string? Label, string? UexLocation)> _place;

    public KioskBoxTracker(GameLogFeed feed, KioskBoxSizeStore store,
                           Func<(string? Label, string? UexLocation)> place)
    {
        _store = store;
        _place = place;
        // includeReplay: a session already in progress when Nexus starts has its kiosk opens behind
        // us in the file, and those answers are as good as a live one.
        _sub = feed.Subscribe(Ingest, includeReplay: true);
    }

    public void Ingest(GameLogEntry entry) => Apply(entry.Raw);

    /// <summary>Public so a test can drive one line without a live feed, the same seam
    /// AutoLoadTracker.Apply and AcceptedRouteTracker.Apply expose.</summary>
    public void Apply(string raw)
    {
        try
        {
            if (!CommodityBoxParser.LooksRelevant(raw)) return;
            if (CommodityBoxParser.Parse(raw) is not { } observed) return;

            // The UEX Location when the alias table recognized the token, the display name
            // otherwise - the same first-hint-then-fall-back order TradeOriginResolver applies.
            var (label, uex) = _place();
            _store.Record(string.IsNullOrWhiteSpace(uex) ? label : uex, observed);
        }
        catch (Exception ex)
        {
            // A parse fault must never break the feed for every other consumer.
            Logger.Info($"[TRADE] kiosk box parse failed: {ex.Message}");
        }
    }

    public void Dispose() => _sub?.Dispose();
}
