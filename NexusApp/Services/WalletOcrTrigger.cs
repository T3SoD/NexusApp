using System.Globalization;
using System.Text.RegularExpressions;

namespace NexusApp.Services;

// Pure parsing for the OCR wallet: the mobiGlas-open trigger line, its clock stamp, and the
// balance inside noisy OCR text. Stateless sibling of CommodityLogParser.
public static class WalletOcrTrigger
{
    // Balances above this are treated as OCR misreads, not wealth.
    public const long MaxPlausibleBalance = 99_999_999_999;

    // Letter-flanked digits are handle leetspeak (PL4Y3R) or a digit misread as a letter
    // ("5,101.94B" with 8 read as B, live 16:44), not money. The run is ATOMIC ((?>...)): a
    // greedy match that lands on a letter fails outright instead of backtracking into a
    // truncated smaller number, which is how "5,101.94B" once became 5,101. The trade-off is
    // documented: a read that glues the label to the number ("aUEC5105256") is rejected too,
    // which only costs one capture; the next scan retries.
    private static readonly Regex DigitGroup =
        new("(?<![A-Za-z0-9])(?>[0-9][0-9.,]*)(?![A-Za-z0-9])", RegexOptions.Compiled);

    // Contract completion HUD notification (2026-08-06 recon): the display name sits between
    // this marker and the text-closing ': " [' before the queue index. Names carry colons, so
    // only that exact closing sequence ends the text; a marker line without it is a multi-line
    // notification fragment and is rejected whole.
    private const string CompleteMarker = "Added notification \"Contract Complete: ";
    private const string NotificationClose = ": \" [";

    // <EM4>...</EM4> spans are rep/beacon meta tags beside the name, dropped whole; any other
    // stray tag drops too.
    private static readonly Regex NotificationMeta =
        new("<EM4>.*?</EM4>|<[^>]*>", RegexOptions.Compiled);
    private static readonly Regex WhitespaceRun = new(@"\s+", RegexOptions.Compiled);

    public static bool IsMobiGlasOpenSignal(string raw) =>
        raw.Contains("<VehicleListQuery>", StringComparison.Ordinal) &&
        raw.Contains("Fetching vehicle list for player", StringComparison.Ordinal);

    /// <summary>Contract completion line: out comes the cleaned display name and the line's own
    /// stamp. The payout amount is NOT here by design: Game.log logs which contract completed,
    /// almost never what it paid (one Awarded line in 395 logs), so the amount stays the wallet
    /// delta's job.</summary>
    public static bool TryParseContractComplete(string raw, out string name, out DateTime utc)
    {
        name = "";
        utc = default;
        var start = raw.IndexOf(CompleteMarker, StringComparison.Ordinal);
        if (start < 0) return false;
        if (!TryParseLineUtc(raw, out utc)) return false;
        start += CompleteMarker.Length;
        var close = raw.IndexOf(NotificationClose, start, StringComparison.Ordinal);
        if (close < 0) return false;
        var cleaned = WhitespaceRun.Replace(
            NotificationMeta.Replace(raw.Substring(start, close - start), " "), " ").Trim();
        if (cleaned.Length == 0) return false;
        name = cleaned;
        return true;
    }

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

    /// <summary>Dual-recognition verdict for one grab (two OCR passes over the same pixels,
    /// inverted and plain). Both parse and agree: the grab counts. Both parse and DISAGREE:
    /// the grab is rejected, because two engines reading different numbers from the same
    /// pixels is the misread signal this exists to catch. Only one parses: it stands, since
    /// the burst's cross-grab agreement still guards a lone misread. Requiring both to read
    /// was tried first and rejected 11 of 12 live grabs (the plain pass fails routinely
    /// against the animated mobiGlas background).</summary>
    public static string? BestRead(string? a, string? b)
    {
        var va = a is null ? null : ExtractBalance(a);
        var vb = b is null ? null : ExtractBalance(b);
        if (va is not null && vb is not null) return va == vb ? a : null;
        if (va is not null) return a;
        return vb is not null ? b : null;
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
