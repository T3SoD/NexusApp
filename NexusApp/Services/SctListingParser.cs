using System.Text.Json;

namespace NexusApp.Services;

// One crowdsourced price observation from SC Trade Tools (/api/crowdsource/commodity-listings,
// verified shape: nexus-assets/specs/sc-trade-tools-raw/openapi.json,
// CrowdsourceCommodityListingsDto). Location/Commodity are free-text SCT names, joined through
// SctUexMap elsewhere (Task 7) - never resolved here. This is a faithful, unenriched parse.
public sealed record SctListing(string Location, string Transaction, string Commodity, double Price,
                                int Quantity, double Saturation, DateTime TimestampUtc);

// Pure parsing of one page of the SCT crowdsource-listings response, and the freshness filter
// every consumer of the raw feed needs: SC Trade Tools' own listings are a LEDGER (median age 38
// days per the divergence benchmark), not a live price table, so nothing downstream may treat an
// unfiltered row as current. Never throws: malformed JSON or an unexpected shape parses to an
// empty list rather than faulting the caller (same trust-boundary posture as MarketParse).
public static class SctListingParser
{
    // One PAGE's response body: {"content":[...], "page":{...}}. "page" metadata (size/number/
    // totalElements/totalPages) is read by the fetch loop (SctMarketService, Task 7), not here -
    // this method only turns "content" into rows.
    public static List<SctListing> Parse(string body, out int skipped)
    {
        var result = new List<SctListing>();
        skipped = 0;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return result;
            if (!root.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                return result;

            foreach (var row in content.EnumerateArray())
            {
                if (row.ValueKind == JsonValueKind.Object
                    && TryStr(row, "location", out var location)
                    && TryStr(row, "transaction", out var transaction)
                    && TryStr(row, "commodity", out var commodity)
                    && TryDouble(row, "price", out var price)
                    && TryInt(row, "quantity", out var quantity)
                    && TryDouble(row, "saturation", out var saturation)
                    && TryTimestamp(row, "timestamp", out var timestampUtc))
                {
                    result.Add(new SctListing(location, transaction, commodity, price, quantity,
                        saturation, timestampUtc));
                }
                else
                {
                    skipped++;
                }
            }
        }
        catch (Exception)
        {
            // Trust-boundary guard, same posture as MarketParse: an encoding/shape surprise this
            // parser did not anticipate resolves to "nothing parsed," never a throw into the caller.
            skipped = 0;
            return new List<SctListing>();
        }
        return result;
    }

    // Rows older than maxAge are dropped: the age is measured from the ROW'S OWN timestamp, never
    // the fetch time (spec: "the age label is the row's own timestamp, not the fetch time").
    public static List<SctListing> Fresh(IReadOnlyList<SctListing> rows, TimeSpan maxAge, DateTime nowUtc) =>
        rows.Where(r => nowUtc - r.TimestampUtc <= maxAge).ToList();

    private static bool TryStr(JsonElement obj, string prop, out string value)
    {
        value = "";
        if (!obj.TryGetProperty(prop, out var el) || el.ValueKind != JsonValueKind.String) return false;
        value = el.GetString() ?? "";
        return true;
    }

    private static bool TryDouble(JsonElement obj, string prop, out double value)
    {
        value = 0;
        if (!obj.TryGetProperty(prop, out var el) || el.ValueKind != JsonValueKind.Number || !el.TryGetDouble(out value))
            return false;
        if (!double.IsFinite(value)) { value = 0; return false; }
        return true;
    }

    private static bool TryInt(JsonElement obj, string prop, out int value)
    {
        value = 0;
        return obj.TryGetProperty(prop, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out value);
    }

    private static bool TryTimestamp(JsonElement obj, string prop, out DateTime utc)
    {
        utc = default;
        if (!obj.TryGetProperty(prop, out var el) || el.ValueKind != JsonValueKind.String) return false;
        var s = el.GetString();
        if (string.IsNullOrEmpty(s)) return false;
        if (!DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var dto))
            return false;
        utc = dto.UtcDateTime;
        return true;
    }
}
