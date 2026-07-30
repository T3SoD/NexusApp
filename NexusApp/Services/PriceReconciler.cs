namespace NexusApp.Services;

public enum PriceSourceState { UexOnly, Corroborated, Disagree, SctOnly }

// value: always UEX's own number, even when the two sources disagree (mock tooltip: "price shown
// here is UEX's" - SCT only ever changes the STATE). disagreePct: 0 unless State is Disagree or
// Corroborated (the pct is meaningful only when both sides were actually compared).
public sealed record ReconciledPrice(double Value, PriceSourceState State, double DisagreePct,
                                     DateTime UexModifiedUtc, DateTime? SctTimestampUtc);

// Pure headless price-corroboration math (trading tab spec, decision 2026-07-29). Never a bare
// number, never null ambiguity: every non-null result carries a tagged state, so a caller can
// never accidentally treat an SCT-only or stale-degraded price as if it were fully corroborated.
public static class PriceReconciler
{
    public static readonly TimeSpan FreshWindow = TimeSpan.FromHours(48);
    public const double AgreeThresholdPct = 3.0;

    public static ReconciledPrice? Reconcile(TradePriceRow? uexRow, string side, SctListing? sct, DateTime nowUtc)
    {
        // side is caller-validated ("buy"|"sell" per contract); anything other than "buy" reads sell.
        double? uexValue = uexRow is null ? null : side == "buy" ? uexRow.Buy : uexRow.Sell;

        // A UEX side price <= 0 is treated as UEX-ABSENT, before any ammo/freshness/pct logic
        // below runs (architect fix, 2026-07-29 review): rows with price 0 are kept BY DESIGN (a
        // terminal that neither buys nor sells today is a real, displayable state - see
        // MarketParse.ParseTradePriceRows), so this is not an edge case, it is a common row. With
        // nothing on the UEX side to corroborate, folding it into the same path as uexRow being
        // null avoids reporting a fabricated Corroborated/Disagree/UexOnly reading at Value=0.
        bool uexUsable = uexValue is > 0;

        if (!uexUsable && sct is null) return null;

        // Architect resolution (2026-07-29): Ship Ammunition commodities never receive
        // corroboration. UEX names these in title case ("Ship Ammunition - Size 3"); SCT names
        // them lower case ("ship ammunition - size 3") - the size-tier split never lines up
        // cleanly enough across the two sources for an agree/disagree reading to mean anything,
        // so this always wins over the ordinary comparison below. Gated on uexUsable (not just
        // uexRow being non-null): a zero-price ammunition row has already folded into the
        // UEX-absent path above and is decided by the SCT-side ammo check just below instead.
        if (uexUsable && IsShipAmmunition(uexRow!.CommodityName))
            return new ReconciledPrice(uexValue!.Value, PriceSourceState.UexOnly, 0, uexRow.ModifiedUtc, sct?.TimestampUtc);

        if (!uexUsable)
        {
            // Same rule, SCT-only direction: an ammunition-only observation isn't corroboration
            // of anything and has no usable UEX row to fall back to, so it is not surfaced at all.
            if (IsShipAmmunition(sct!.Commodity)) return null;
            return new ReconciledPrice(sct.Price, PriceSourceState.SctOnly, 0, default, sct.TimestampUtc);
        }

        if (sct is null)
            return new ReconciledPrice(uexValue!.Value, PriceSourceState.UexOnly, 0, uexRow!.ModifiedUtc, null);

        bool uexFresh = nowUtc - uexRow!.ModifiedUtc <= FreshWindow;
        bool sctFresh = nowUtc - sct.TimestampUtc <= FreshWindow;
        if (!uexFresh || !sctFresh)
            // A stale second source corroborates nothing: this degrades to the same UexOnly state
            // a missing SCT row would produce, rather than a false Corroborated/Disagree claim.
            // SctTimestampUtc is still populated (there WAS a second source, just too old) - only
            // the STATE, not this field, is what tells a caller not to trust it as corroboration.
            return new ReconciledPrice(uexValue!.Value, PriceSourceState.UexOnly, 0, uexRow.ModifiedUtc, sct.TimestampUtc);

        // uexUsable guarantees uexValue.Value > 0 here (the compiler cannot narrow a plain bool
        // flag the way it narrows a direct null check, hence the ! below), so this division is
        // always safe.
        double pct = Math.Abs(sct.Price - uexValue!.Value) / uexValue.Value * 100.0;
        var state = pct <= AgreeThresholdPct ? PriceSourceState.Corroborated : PriceSourceState.Disagree;
        return new ReconciledPrice(uexValue.Value, state, pct, uexRow.ModifiedUtc, sct.TimestampUtc);
    }

    private static bool IsShipAmmunition(string commodityName) =>
        commodityName.StartsWith("Ship Ammunition", StringComparison.OrdinalIgnoreCase);
}
