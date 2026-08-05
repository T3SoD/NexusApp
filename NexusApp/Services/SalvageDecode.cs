using NexusApp.Models;

namespace NexusApp.Services;

// Salvageable ship-debris panels signature at exactly 2000 RS each (datamined: every
// salvageablerepairable_shipdebris_* DataCore record carries 2000 as its only nonzero
// signature), so an n*2000 reading is n panels. The Resource here is synthetic and decode-only:
// it never enters the database, so the Codex, cart and refinery surfaces never see it. Genuine
// signature collisions with ore multiples surface as two cards, which is honest - the game
// cannot tell them apart either. Under the cluster caps the overlap is close-band only (e.g.
// 25890 close-matches both Ice x6 and 13 panels); no in-cap exact ore multiple of the current
// seed lands on n*2000. Issue #34.
public static class SalvageDecode
{
    public const int PanelRs = 2000;

    // A fresh instance per call: Resource is mutable (IsPinned), so a shared static template
    // could leak state across scans.
    public static Resource Template() => new()
    {
        Name = "Salvage", BaseRs = PanelRs, Method = "salvage", Tier = "", Rarity = "",
    };

    public static RsMatch? TryMatch(int rs)
    {
        var t = Template();
        var (matches, panels, isExact, errorPct) = t.CheckRs(rs);
        return matches ? new RsMatch(t, panels, isExact, errorPct) : null;
    }
}
