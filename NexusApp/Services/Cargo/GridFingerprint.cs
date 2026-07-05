using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using NexusApp.Models.Cargo;

namespace NexusApp.Services.Cargo;

// A stable content fingerprint of a ship's effective cargo-grid set. A sign-off records the fingerprint
// of the grids that were reviewed; if the embedded/override geometry later changes, the fingerprints no
// longer match and the sign-off is shown as "reviewed against older data" (non-destructive; the owner
// re-confirms). Canonicalizes each grid to a culture-invariant string and sorts by id, so grid ORDER
// never affects the result, but any dimension / position / accepted-set / orientation change does.
public static class GridFingerprint
{
    public static string Of(IReadOnlyList<GridDef> grids) =>
        Hash(grids.OrderBy(g => g.Id).Select(Canon));

    private static string Canon(GridDef g)
    {
        var ci = CultureInfo.InvariantCulture;
        var acc = string.Join("/", g.AcceptedCaps.OrderBy(a => a));
        string P(double? v) => v.HasValue ? v.Value.ToString("0.###", ci) : "_";   // null (schematic) != 0
        return string.Join("|", g.Id, g.W, g.D, g.H, acc, P(g.PosX), P(g.PosY), P(g.PosZ), g.WAlongShipY ? "1" : "0");
    }

    private static string Hash(IEnumerable<string> rows)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", rows)));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
