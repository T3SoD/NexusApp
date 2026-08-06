using System.Globalization;
using System.Text.RegularExpressions;

namespace NexusApp.Services;

// Pure parsing for the OCR wallet: the mobiGlas-open trigger line, its clock stamp, and the
// balance inside noisy OCR text. Stateless sibling of CommodityLogParser.
public static class WalletOcrTrigger
{
    // Balances above this are treated as OCR misreads, not wealth.
    public const long MaxPlausibleBalance = 99_999_999_999;

    // Letter-flanked digits are handle leetspeak (PL4Y3R), not money: the wallet region can
    // catch the player handle under the balance (live calibration, 2026-08-06). The trade-off
    // is documented: a read that glues the label to the number ("aUEC5105256") is rejected too,
    // which only costs one capture; the next scan retries.
    private static readonly Regex DigitGroup =
        new("(?<![A-Za-z0-9])[0-9][0-9.,]*(?![A-Za-z0-9])", RegexOptions.Compiled);

    public static bool IsMobiGlasOpenSignal(string raw) =>
        raw.Contains("<VehicleListQuery>", StringComparison.Ordinal) &&
        raw.Contains("Fetching vehicle list for player", StringComparison.Ordinal);

    public static bool TryParseLineUtc(string raw, out DateTime utc)
    {
        utc = default;
        if (raw.Length < 26 || raw[0] != '<' || raw[25] != '>') return false;
        if (!DateTimeOffset.TryParseExact(raw.Substring(1, 24), "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto))
            return false;
        utc = dto.UtcDateTime;
        return true;
    }

    // The wallet region reads more than the number (labels, the clock, flicker). The balance is
    // the digit group with the most digits; comma and period both count as thousands separators.
    public static long? ExtractBalance(string ocrText)
    {
        if (string.IsNullOrEmpty(ocrText)) return null;
        string? best = null;
        var bestDigits = 0;
        foreach (Match m in DigitGroup.Matches(ocrText))
        {
            var digits = 0;
            foreach (var c in m.Value)
                if (c is >= '0' and <= '9') digits++;
            if (digits > bestDigits)
            {
                bestDigits = digits;
                best = m.Value;
            }
        }
        if (best is null) return null;
        var cleaned = best.Replace(",", "").Replace(".", "");
        if (!long.TryParse(cleaned, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            return null;
        return value > MaxPlausibleBalance ? null : value;
    }
}
