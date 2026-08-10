using System.Globalization;

namespace NexusApp.Models;

public enum TransactionKind { Buy, Sell }

// One box-size group from a request's Cargo Box Data tail. Buys log one group; sells can carry
// several, one per box size in the consignment.
public sealed record CargoBoxGroup(decimal BoxSize, int UnitAmount);

// One commodity-kiosk settlement from the Game.log. Immutable except Voided: an error response
// inside SessionLedger's void window flips it, and a voided row never counts toward totals.
public sealed class CommodityTransaction
{
    public DateTime TimestampUtc { get; init; }        // the line's own <...Z> stamp
    public TransactionKind Kind { get; init; }
    public long Amount { get; init; }                  // aUEC, positive; Kind carries the sign
    public decimal Scu { get; init; }                  // always SCU: buys arrive as cSCU and are divided by 100 at parse
    public string ResourceGuid { get; init; } = "";    // stored unresolved; commodity names arrive with D10
    public string ShopName { get; init; } = "";        // raw token; cleaned for display, raw in tooltip
    public string ShopId { get; init; } = "";
    public string KioskId { get; init; } = "";
    public bool AutoLoading { get; init; }
    public IReadOnlyList<CargoBoxGroup> Boxes { get; init; } = Array.Empty<CargoBoxGroup>();
    public string? Voided { get; set; }                // null = settled; else the result code that refused it

    // Where the player was when the settlement logged, stamped by ProfitTracker from the location
    // tracker's state at that line (same feed, subscribed ahead of the profit consumer, so replay
    // stamps identically). Null = no location signal yet. The shop token cannot carry this:
    // kiosk shopNames are shared TEMPLATES ("SCShop_Admin_lt_base_g" at three different kiosks,
    // recon 2026-08-02, confirmed live at MIC-L5 2026-08-05), so they name a kiosk CLASS, not a place.
    public string? PlaceLabel { get; set; }
    public bool PlaceIsArea { get; set; }              // jurisdiction reading: render "{label} space", dim

    // The UEX Location string for the same settlement (trade/cargo fusion spec 2026-08-09 section
    // 1.5, task D1), stamped alongside PlaceLabel from the same location-tracker read
    // (LocationTracker.LastKnownUexLocation). PlaceLabel is a DISPLAY string and is documented to
    // fail on real captured locations (some in-game names simply do not appear anywhere in UEX's
    // own Location/Name vocabulary - see TradeOriginResolver.TerminalIdsForLocation); this is the
    // precise UEX Location value TerminalIdsForLocation's own uexLocation hint expects, so a route
    // matcher stands a materially better chance of resolving the terminal a transaction happened
    // at. Null exactly when PlaceLabel is: no location signal yet, or the token had no UEX alias.
    public string? PlaceUexLocation { get; set; }

    // Replay-dedupe identity: two settlements cannot share timestamp, kind, amount, kiosk and
    // resource inside one millisecond.
    public string Key => string.Create(CultureInfo.InvariantCulture,
        $"{TimestampUtc:O}|{Kind}|{Amount}|{KioskId}|{ResourceGuid}");
}
