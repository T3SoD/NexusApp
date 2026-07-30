using System.Linq;
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

public class SctListingParserTests
{
    // Real page body, trimmed from sct-uex-benchmark-raw\sct_listings.json (field values verbatim),
    // wrapped in the real PagedModelCrowdsourceCommodityListingsDto envelope (openapi.json).
    private const string RealPageBody = """
    {"content":[
      {"location":"stanton > mic l2 > mic-l2 long forest station","transaction":"SELLS","commodity":"waste","price":115,"quantity":1,"saturation":0.6666666666666666,"boxSizesInScu":null,"batchId":"dcc4ba21-b697-4851-8982-32d7d1a49141","timestamp":"2026-07-29T10:32:38-04:00"},
      {"location":"stanton > mic l2 > mic-l2 long forest station","transaction":"SELLS","commodity":"scrap","price":2990,"quantity":2100,"saturation":1.0,"boxSizesInScu":null,"batchId":"dcc4ba21-b697-4851-8982-32d7d1a49141","timestamp":"2026-07-29T10:32:38-04:00"},
      {"location":"stanton > mic l2 > mic-l2 long forest station","transaction":"SELLS","commodity":"diamond","price":5759,"quantity":9,"saturation":0.16666666666666666,"boxSizesInScu":null,"batchId":"dcc4ba21-b697-4851-8982-32d7d1a49141","timestamp":"2026-07-29T10:32:38-04:00"},
      {"location":"stanton > mic l2 > mic-l2 long forest station","transaction":"SELLS","commodity":"astatine","price":2649,"quantity":6,"saturation":0.16666666666666666,"boxSizesInScu":null,"batchId":"dcc4ba21-b697-4851-8982-32d7d1a49141","timestamp":"2026-07-29T10:32:38-04:00"},
      {"location":"stanton > hur l1 > hur-l1 green glade station","transaction":"BUYS","commodity":"stims","price":5300,"quantity":118,"saturation":0.16666666666666666,"boxSizesInScu":null,"batchId":"4f8eba9a-8cff-4394-b60e-ffe983b4374e","timestamp":"2026-07-29T09:31:05-04:00"},
      {"location":"stanton > hur l1 > hur-l1 green glade station","transaction":"BUYS","commodity":"hydrogen","price":1100,"quantity":0,"saturation":0.0,"boxSizesInScu":null,"batchId":"4f8eba9a-8cff-4394-b60e-ffe983b4374e","timestamp":"2026-07-29T09:31:05-04:00"},
      {"location":"stanton > hur l1 > hur-l1 green glade station","transaction":"BUYS","commodity":"processed food","price":1500,"quantity":0,"saturation":0.0,"boxSizesInScu":null,"batchId":"4f8eba9a-8cff-4394-b60e-ffe983b4374e","timestamp":"2026-07-29T09:31:05-04:00"},
      {"location":"stanton > hur l1 > hur-l1 green glade station","transaction":"BUYS","commodity":"distilled spirits","price":1900,"quantity":0,"saturation":0.0,"boxSizesInScu":null,"batchId":"4f8eba9a-8cff-4394-b60e-ffe983b4374e","timestamp":"2026-07-29T09:31:05-04:00"},
      {"location":"stanton > hurston > everus harbor","transaction":"BUYS","commodity":"year of the dog envelope","price":2000000,"quantity":0,"saturation":0.0,"boxSizesInScu":null,"batchId":"008f0f90-361c-4a7b-a31a-6038de905ddd","timestamp":"2026-07-29T05:09:59-04:00"},
      {"location":"stanton > hurston > everus harbor","transaction":"BUYS","commodity":"corundum","price":3800,"quantity":1537,"saturation":0.3333333333333333,"boxSizesInScu":null,"batchId":"008f0f90-361c-4a7b-a31a-6038de905ddd","timestamp":"2026-07-29T05:09:59-04:00"},
      {"location":"stanton > hurston > everus harbor","transaction":"BUYS","commodity":"fluorine","price":1300,"quantity":0,"saturation":0.0,"boxSizesInScu":null,"batchId":"008f0f90-361c-4a7b-a31a-6038de905ddd","timestamp":"2026-07-29T05:09:59-04:00"},
      {"location":"stanton > hurston > everus harbor","transaction":"BUYS","commodity":"luminalia gift","price":5000000,"quantity":0,"saturation":0.0,"boxSizesInScu":null,"batchId":"008f0f90-361c-4a7b-a31a-6038de905ddd","timestamp":"2026-07-29T05:09:59-04:00"},
      {"location":"nyx gateway","transaction":"BUYS","commodity":"stileron","price":150000,"quantity":95,"saturation":0.8333333333333334,"boxSizesInScu":null,"batchId":"b5f9bd7d-e8d0-41d0-926f-1c7669193699","timestamp":"2026-07-28T10:52:49-04:00"},
      {"location":"nyx gateway","transaction":"BUYS","commodity":"stims","price":5500,"quantity":0,"saturation":0.0,"boxSizesInScu":null,"batchId":"b5f9bd7d-e8d0-41d0-926f-1c7669193699","timestamp":"2026-07-28T10:52:49-04:00"},
      {"location":"nyx gateway","transaction":"BUYS","commodity":"medical supplies","price":4800,"quantity":321,"saturation":0.3333333333333333,"boxSizesInScu":null,"batchId":"b5f9bd7d-e8d0-41d0-926f-1c7669193699","timestamp":"2026-07-28T10:52:49-04:00"},
      {"location":"sheperd's rest","transaction":"BUYS","commodity":"processed food","price":1200,"quantity":0,"saturation":0.0,"boxSizesInScu":null,"batchId":"f5a8cd81-0c92-4b20-8f80-625d239d0729","timestamp":"2026-07-25T16:00:32-04:00"},
      {"location":"sheperd's rest","transaction":"BUYS","commodity":"revenant pod","price":11000,"quantity":0,"saturation":0.0,"boxSizesInScu":null,"batchId":"f5a8cd81-0c92-4b20-8f80-625d239d0729","timestamp":"2026-07-25T16:00:32-04:00"},
      {"location":"sheperd's rest","transaction":"BUYS","commodity":"nitrogen","price":3000,"quantity":0,"saturation":0.0,"boxSizesInScu":null,"batchId":"f5a8cd81-0c92-4b20-8f80-625d239d0729","timestamp":"2026-07-25T16:00:32-04:00"},
      {"location":"stanton > arccorp > baijini point","transaction":"SELLS","commodity":"ship ammunition - size 5","price":7384,"quantity":12000,"saturation":1.0,"boxSizesInScu":null,"batchId":"f5bced9d-0470-459b-9e05-65c2903cbcfb","timestamp":"2026-07-19T07:06:10-04:00"},
      {"location":"stanton > arccorp > baijini point","transaction":"SELLS","commodity":"corundum","price":3015,"quantity":1144,"saturation":0.3333333333333333,"boxSizesInScu":null,"batchId":"f5bced9d-0470-459b-9e05-65c2903cbcfb","timestamp":"2026-07-19T07:06:10-04:00"},
      {"location":"stanton > arccorp > baijini point","transaction":"SELLS","commodity":"titanium","price":27681,"quantity":5891,"saturation":1.0,"boxSizesInScu":null,"batchId":"f5bced9d-0470-459b-9e05-65c2903cbcfb","timestamp":"2026-07-19T07:06:10-04:00"},
      {"location":"nyx > levski","transaction":"BUYS","commodity":"laranite","price":7800,"quantity":0,"saturation":0.0,"boxSizesInScu":null,"batchId":"9e4adc86-f1b9-4464-afad-1beb034f624c","timestamp":"2026-07-19T11:28:57-04:00"},
      {"location":"stanton > crusader > yela > grim hex","transaction":"BUYS","commodity":"revenant tree pollen","price":1000,"quantity":0,"saturation":0.0,"boxSizesInScu":null,"batchId":"b388934f-9c75-4e6e-b0a3-462bafb3e381","timestamp":"2026-05-19T17:38:05-04:00"},
      {"location":"stanton > crusader > yela > grim hex","transaction":"BUYS","commodity":"amioshi plague","price":23000,"quantity":0,"saturation":0.0,"boxSizesInScu":null,"batchId":"b388934f-9c75-4e6e-b0a3-462bafb3e381","timestamp":"2026-05-19T17:38:05-04:00"},
      {"location":"stanton > crusader > yela > grim hex","transaction":"BUYS","commodity":"taranite","price":22000,"quantity":0,"saturation":0.0,"boxSizesInScu":null,"batchId":"b388934f-9c75-4e6e-b0a3-462bafb3e381","timestamp":"2026-05-19T17:38:05-04:00"},
      {"location":"stanton > crusader > yela > grim hex","transaction":"BUYS","commodity":"corundum","price":3000,"quantity":0,"saturation":0.0,"boxSizesInScu":null,"batchId":"b388934f-9c75-4e6e-b0a3-462bafb3e381","timestamp":"2026-05-19T17:38:05-04:00"}
    ],"page":{"size":100,"number":0,"totalElements":26,"totalPages":1}}
    """;

    // 2026-07-29 18:00 UTC: a few hours after the freshest real row (2026-07-29T10:32:38-04:00 =
    // 14:32:38 UTC) so age math below is unambiguous and not tied to whenever this suite happens
    // to run for real (a real DateTime.UtcNow would make 2026-dated fixture rows a ticking time
    // bomb the moment the real calendar rolls past them - see Task 7's Reconcile-style nowUtc note).
    private static readonly DateTime NowUtc = new(2026, 7, 29, 18, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Parse_RealFixture_AllRowsParse_NoneSkipped()
    {
        var rows = SctListingParser.Parse(RealPageBody, out var skipped);
        Assert.Equal(26, rows.Count);
        Assert.Equal(0, skipped);
    }

    [Fact]
    public void Parse_AmbiguousBareLocation_ParsesAsIs()
    {
        // "nyx gateway" (no system/station qualifier) is real SCT crowdsourced data - ambiguous
        // between "Nyx > Pyro Gateway" and "Nyx > Stanton Gateway" in the map. Parsing does not
        // resolve or reject it; Task 7's join is what drops it.
        var rows = SctListingParser.Parse(RealPageBody, out _);
        var row = rows.Single(r => r.Commodity == "stileron");
        Assert.Equal("nyx gateway", row.Location);
        Assert.Equal("BUYS", row.Transaction);
        Assert.Equal(150000, row.Price);
        Assert.Equal(95, row.Quantity);
        Assert.Equal(0.8333333333333334, row.Saturation, precision: 10);
        Assert.Equal(new DateTime(2026, 7, 28, 14, 52, 49, DateTimeKind.Utc), row.TimestampUtc);
    }

    [Fact]
    public void Parse_TypoLocation_ParsesAsIs()
    {
        // "sheperd's rest" - a real crowdsourced misspelling of the map's "Pyro > Bloom >
        // Shepherd's Rest". Parsing keeps it verbatim; the join (Task 7) cannot match it either.
        var rows = SctListingParser.Parse(RealPageBody, out _);
        var row = rows.Single(r => r.Commodity == "revenant pod");
        Assert.Equal("sheperd's rest", row.Location);
    }

    [Fact]
    public void Parse_MalformedBody_ReturnsEmpty_NoThrow()
    {
        var rows = SctListingParser.Parse("{ not json", out var skipped);
        Assert.Empty(rows);
        Assert.Equal(0, skipped);
    }

    [Fact]
    public void Parse_RowMissingRequiredField_IsSkippedNotFatal()
    {
        const string body = """
        {"content":[
          {"location":"stanton > hurston > everus harbor","transaction":"BUYS","commodity":"corundum","price":3800,"quantity":1537,"saturation":0.3333333333333333,"timestamp":"2026-07-29T05:09:59-04:00"},
          {"location":"stanton > hurston > everus harbor","transaction":"BUYS","commodity":"fluorine","quantity":0,"saturation":0.0,"timestamp":"2026-07-29T05:09:59-04:00"}
        ],"page":{"size":100,"number":0,"totalElements":2,"totalPages":1}}
        """;
        // Row 2 has no "price" - required per CrowdsourceCommodityListingsDto.
        var rows = SctListingParser.Parse(body, out var skipped);
        Assert.Single(rows);
        Assert.Equal(1, skipped);
    }

    [Fact]
    public void Fresh_RealFixture_DropsRowsOlderThanSevenDays()
    {
        var rows = SctListingParser.Parse(RealPageBody, out _);
        var fresh = SctListingParser.Fresh(rows, TimeSpan.FromDays(7), NowUtc);
        Assert.Equal(18, fresh.Count);
        Assert.DoesNotContain(fresh, r => r.Location == "stanton > crusader > yela > grim hex");
        Assert.DoesNotContain(fresh, r => r.Location == "nyx > levski");
        Assert.Contains(fresh, r => r.Location == "nyx gateway");   // fresh AND ambiguous are independent axes
    }

    [Fact]
    public void Fresh_BoundaryIsInclusive()
    {
        var now = new DateTime(2026, 7, 29, 12, 0, 0, DateTimeKind.Utc);
        var maxAge = TimeSpan.FromDays(7);
        var exactlyAtCutoff = new SctListing("x", "SELLS", "waste", 1, 1, 0, now - maxAge);
        var oneTickPastCutoff = new SctListing("x", "SELLS", "waste", 1, 1, 0, now - maxAge - TimeSpan.FromTicks(1));
        var fresh = SctListingParser.Fresh(new[] { exactlyAtCutoff, oneTickPastCutoff }, maxAge, now);
        Assert.Single(fresh);
        Assert.Equal(exactlyAtCutoff, fresh[0]);
    }
}
