using System.Globalization;
using System.Text.RegularExpressions;

namespace NexusApp.Services;

/// <summary>What one kiosk will actually sell a commodity in, as the kiosk itself reported it.</summary>
public sealed record KioskBoxSizes(DateTime TimestampUtc, string ShopId, string ShopName,
                                   string CommodityName, IReadOnlyList<int> Sizes);

/// <summary>
/// Parses the box sizes a commodity kiosk advertises when it opens (2026-08-10).
///
/// <para>Line shape, verified live against build 12344265:</para>
/// <code>
/// &lt;CEntityComponentCommodityUIProvider::LoadShopInventoryData::&lt;lambda_1&gt;::operator ()&gt;
///   AddingCommodityBox - playerId[..] shopId[747398598793] shopName[SCShop_Admin_lt_base_g]
///   commodityName[ResourceType.Scrap] Available Box Sizes:  boxSize[1] boxSize[2] boxSize[4]
///   boxSize[8] boxSize[16]
/// </code>
///
/// <para>WHY THIS MATTERS. The planner snaps a trip down to the box sizes the buy terminal can
/// supply, and until now the only source for that was UEX's shipped container_sizes, which is one
/// list per commodity row and does not vary by terminal. This line is the kiosk's own answer and
/// it demonstrably DOES vary: at MIC-L4's admin kiosk, Scrap offers 1/2/4/8/16 while Waste at the
/// same kiosk offers up to 32. Pricing a trip in 32 SCU boxes at a kiosk that only stocks 16s
/// produces a quantity nobody can actually buy.</para>
///
/// <para>It carries no QUANTITY. Stock levels are not in Game.log at all - re-confirmed by searching
/// a live session for an on-screen figure of 1713 SCU and finding it in no form, decimal or
/// centi-SCU. This is box sizes only.</para>
///
/// <para>playerId is matched but never read, the same rule CommodityLogParser follows.</para>
/// </summary>
public static class CommodityBoxParser
{
    private static readonly Regex Line = new(
        @"^<(?<ts>\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z)>.*?AddingCommodityBox\b" +
        @".*?shopId\[(?<shop>[^\]]*)\]" +
        @".*?shopName\[(?<name>[^\]]*)\]" +
        @".*?commodityName\[(?<commodity>[^\]]*)\]",
        RegexOptions.Compiled);

    private static readonly Regex Size = new(@"boxSize\[(?<n>\d+)\]", RegexOptions.Compiled);

    /// <summary>Cheap pre-filter, the LooksHaulRelevant idiom: keeps the regex off every line.</summary>
    public static bool LooksRelevant(string raw) => raw.Contains("AddingCommodityBox", StringComparison.Ordinal);

    /// <summary>Parses one line, or null when it is not this shape or carries no sizes.</summary>
    public static KioskBoxSizes? Parse(string raw)
    {
        if (string.IsNullOrEmpty(raw) || !LooksRelevant(raw)) return null;
        var m = Line.Match(raw);
        if (!m.Success) return null;

        var sizes = new List<int>();
        for (var s = Size.Match(raw, m.Index); s.Success; s = s.NextMatch())
            if (int.TryParse(s.Groups["n"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0)
                sizes.Add(n);
        if (sizes.Count == 0) return null;   // a kiosk that advertises nothing tells us nothing

        return new KioskBoxSizes(
            ParseStamp(m.Groups["ts"].Value),
            m.Groups["shop"].Value,
            m.Groups["name"].Value,
            CommodityName(m.Groups["commodity"].Value),
            sizes.Distinct().OrderBy(n => n).ToList());
    }

    /// <summary>"ResourceType.Scrap" is the game's own vocabulary; every other surface in this app
    /// speaks UEX commodity names, so the prefix comes off here rather than at each call site.
    /// A value with no prefix passes through unchanged.</summary>
    public static string CommodityName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var dot = raw.LastIndexOf('.');
        return dot >= 0 && dot < raw.Length - 1 ? raw[(dot + 1)..] : raw;
    }

    /// <summary>The container-sizes string the planner already speaks (TradePriceRow.ContainerSizes,
    /// "1,2,4,8,16"), so an observation can be swapped in wherever UEX's own list is read.</summary>
    public static string ToContainerSizes(IEnumerable<int> sizes) =>
        string.Join(",", sizes.Distinct().OrderBy(n => n));

    private static DateTime ParseStamp(string ts) =>
        DateTime.ParseExact(ts, "yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}
