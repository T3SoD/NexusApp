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

    // Replay-dedupe identity: two settlements cannot share timestamp, kind, amount, kiosk and
    // resource inside one millisecond.
    public string Key => string.Create(CultureInfo.InvariantCulture,
        $"{TimestampUtc:O}|{Kind}|{Amount}|{KioskId}|{ResourceGuid}");
}
