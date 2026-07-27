using System.Text.Json;
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// MarketSnapshot is a plain data holder (Task 3 adds file persistence); MarketParse is the
// tolerant UEX response reader. Fixtures here are small hand-written envelopes, not recorded
// payloads, per the plan's "never paste megabytes" rule.
public class MarketSnapshotTests
{
    private const string OkEnvelope = """{"status":"ok","http_code":200,"data":[],"message":""}""";
    private const string NotAllowedEnvelope = """{"status":"not_allowed","http_code":403,"data":null,"message":"Not allowed"}""";
    private const string HtmlBody = "<html><body>Not Found</body></html>";

    // --- TryGetData ---------------------------------------------------

    [Fact]
    public void TryGetData_OkEnvelope_ReturnsTrue()
    {
        Assert.True(MarketParse.TryGetData(OkEnvelope, out var data));
        Assert.Equal(JsonValueKind.Array, data.ValueKind);
    }

    [Fact]
    public void TryGetData_NotAllowedStatus_ReturnsFalse() =>
        Assert.False(MarketParse.TryGetData(NotAllowedEnvelope, out _));

    [Fact]
    public void TryGetData_HtmlBody_ReturnsFalse() =>
        Assert.False(MarketParse.TryGetData(HtmlBody, out _));

    [Fact]
    public void TryGetData_EmptyString_ReturnsFalse() =>
        Assert.False(MarketParse.TryGetData("", out _));

    [Fact]
    public void TryGetData_MissingDataProperty_ReturnsFalse() =>
        Assert.False(MarketParse.TryGetData("""{"status":"ok","http_code":200,"message":""}""", out _));

    [Fact]
    public void TryGetData_JsonArrayRoot_ReturnsFalse() =>
        Assert.False(MarketParse.TryGetData("[1,2,3]", out _));

    // Fix-round regression: JsonDocument.Parse(string) throws ArgumentException (not
    // JsonException) on a lone UTF-16 surrogate. Must resolve to false, never throw.
    [Fact]
    public void TryGetData_UnpairedSurrogate_ReturnsFalseNoThrow()
    {
        string body = "{\"status\":\"ok\",\"data\":[{\"id\":1,\"name\":\"Bad\uD800Name\"}]}";

        var ex = Record.Exception(() => MarketParse.TryGetData(body, out _));

        Assert.Null(ex);
        Assert.False(MarketParse.TryGetData(body, out _));
    }

    // --- ParseCommodities -----------------------------------------------

    [Fact]
    public void ParseCommodities_OneRowMissingIdParent_SkipsThatRowOnly()
    {
        const string body = """
        {"status":"ok","http_code":200,"data":[
          {"id":10,"name":"Bexalite (Raw)","slug":"bexalite-raw","is_raw":1,"is_refined":0,"id_parent":11},
          {"id":11,"name":"Bexalite","slug":"bexalite","is_raw":0,"is_refined":1,"id_parent":0},
          {"id":99,"name":"Broken Row","slug":"broken","is_raw":1,"is_refined":0}
        ],"message":""}
        """;

        var rows = MarketParse.ParseCommodities(body, out var skipped);

        Assert.Equal(2, rows.Count);
        Assert.Equal(1, skipped);
        Assert.Equal(new MarketCommodity(10, "Bexalite (Raw)", "bexalite-raw", true, false, 11), rows[0]);
        Assert.Equal(new MarketCommodity(11, "Bexalite", "bexalite", false, true, 0), rows[1]);
    }

    // The live API shape: rows carry "code" and no "slug" at all. Slug is display only, so a
    // missing one must never skip a row (it did on the first real run, and cost all 204).
    [Fact]
    public void ParseCommodities_LiveShapeWithCodeAndNoSlug_KeepsEveryRow()
    {
        const string body = """
        {"status":"ok","http_code":200,"data":[
          {"id":10,"name":"Bexalite (Raw)","code":"BEXA","is_raw":1,"is_refined":0,"id_parent":11},
          {"id":11,"name":"Bexalite","code":"BEXAR","is_raw":0,"is_refined":1,"id_parent":0}
        ],"message":""}
        """;

        var rows = MarketParse.ParseCommodities(body, out var skipped);

        Assert.Equal(0, skipped);
        Assert.Equal(new MarketCommodity(10, "Bexalite (Raw)", "bexa", true, false, 11), rows[0]);
        Assert.Equal(new MarketCommodity(11, "Bexalite", "bexar", false, true, 0), rows[1]);
    }

    [Fact]
    public void ParseCommodities_NoSlugAndNoCode_KeepsRowWithEmptySlug()
    {
        const string body = """
        {"status":"ok","data":[
          {"id":10,"name":"Bexalite (Raw)","is_raw":1,"is_refined":0,"id_parent":11}
        ],"message":""}
        """;

        var rows = MarketParse.ParseCommodities(body, out var skipped);

        Assert.Equal(0, skipped);
        Assert.Equal("", rows[0].Slug);
    }

    // A slug that is present but empty falls through to code, so a blank field never wins.
    [Fact]
    public void ParseCommodities_EmptySlug_FallsBackToCode()
    {
        const string body = """
        {"status":"ok","data":[
          {"id":10,"name":"Bexalite (Raw)","slug":"","code":"BEXA","is_raw":1,"is_refined":0,"id_parent":11}
        ],"message":""}
        """;

        var rows = MarketParse.ParseCommodities(body, out var skipped);

        Assert.Equal(0, skipped);
        Assert.Equal("bexa", rows[0].Slug);
    }

    [Fact]
    public void ParseCommodities_NonzeroFlag_MapsToTrue()
    {
        const string body = """
        {"status":"ok","data":[
          {"id":1,"name":"Weird","slug":"weird","is_raw":2,"is_refined":0,"id_parent":0}
        ],"message":""}
        """;

        var rows = MarketParse.ParseCommodities(body, out var skipped);

        Assert.Equal(0, skipped);
        Assert.True(rows[0].IsRaw);
    }

    [Fact]
    public void ParseCommodities_BadEnvelope_ReturnsEmptyWithZeroSkipped()
    {
        var rows = MarketParse.ParseCommodities(NotAllowedEnvelope, out var skipped);
        Assert.Empty(rows);
        Assert.Equal(0, skipped);
    }

    // Fix-round regression: same unpaired-surrogate escape as the TryGetData case, but through a
    // public Parse* method, to prove the broadened trust-boundary catch actually stops it there.
    [Fact]
    public void ParseCommodities_UnpairedSurrogate_ReturnsEmptyNoThrow()
    {
        string body = "{\"status\":\"ok\",\"data\":[{\"id\":1,\"name\":\"Bad\uD800Name\",\"slug\":\"x\",\"is_raw\":1,\"is_refined\":0,\"id_parent\":0}]}";

        var ex = Record.Exception(() => MarketParse.ParseCommodities(body, out _));

        Assert.Null(ex);
        var rows = MarketParse.ParseCommodities(body, out var skipped);
        Assert.Empty(rows);
        Assert.Equal(0, skipped);
    }

    // --- ParsePriceRows (shared by raw and refined) ----------------------

    [Fact]
    public void ParsePriceRows_FullFieldsRow_ParsesAllValuesAndConvertsEpoch()
    {
        const string body = """
        {"status":"ok","data":[
          {"id_terminal":1,"id_commodity":10,"price_sell":8210,"price_sell_avg_week":8100,
           "game_version":"4.9","date_modified":1784939404,"terminal_name":"Refinery Ore Sales - ARC-L1"}
        ],"message":""}
        """;

        var rows = MarketParse.ParsePriceRows(body, out var skipped);

        Assert.Equal(0, skipped);
        var row = Assert.Single(rows);
        Assert.Equal(new MarketPriceRow(1, 10, 8210, 8100, "4.9",
            new DateTime(2026, 7, 25, 0, 30, 4, DateTimeKind.Utc), "Refinery Ore Sales - ARC-L1"), row);
    }

    [Fact]
    public void ParsePriceRows_StringWhereNumberExpected_SkipsThatRow()
    {
        const string body = """
        {"status":"ok","data":[
          {"id_terminal":1,"id_commodity":10,"price_sell":"not a number","price_sell_avg_week":100,
           "game_version":"4.9","date_modified":1784939404,"terminal_name":"Bad Row"},
          {"id_terminal":2,"id_commodity":10,"price_sell":7000,"price_sell_avg_week":6800,
           "game_version":"4.9","date_modified":1784939404,"terminal_name":"Good Row"}
        ],"message":""}
        """;

        var rows = MarketParse.ParsePriceRows(body, out var skipped);

        Assert.Equal(1, skipped);
        var row = Assert.Single(rows);
        Assert.Equal("Good Row", row.TerminalName);
    }

    [Fact]
    public void ParsePriceRows_WeekAvgZero_IsKeptNotSkipped()
    {
        const string body = """
        {"status":"ok","data":[
          {"id_terminal":3,"id_commodity":10,"price_sell":7000,"price_sell_avg_week":0,
           "game_version":"4.9","date_modified":1784939404,"terminal_name":"Week Zero Terminal"}
        ],"message":""}
        """;

        var rows = MarketParse.ParsePriceRows(body, out var skipped);

        Assert.Equal(0, skipped);
        var row = Assert.Single(rows);
        Assert.Equal(0, row.SellAvgWeek);
        Assert.Equal(7000, row.Sell);
    }

    [Fact]
    public void ParsePriceRows_NegativeEpoch_SkipsRow()
    {
        const string body = """
        {"status":"ok","data":[
          {"id_terminal":1,"id_commodity":10,"price_sell":100,"price_sell_avg_week":100,
           "game_version":"4.9","date_modified":-5,"terminal_name":"Bad Date"}
        ],"message":""}
        """;

        var rows = MarketParse.ParsePriceRows(body, out var skipped);

        Assert.Empty(rows);
        Assert.Equal(1, skipped);
    }

    // Fix-round regression: DateTimeOffset.FromUnixTimeSeconds throws ArgumentOutOfRangeException
    // above 253402300799 (maps past DateTime.MaxValue); the row must be skipped, not throw.
    [Fact]
    public void ParsePriceRows_EpochAboveDateTimeMax_SkipsRowNoThrow()
    {
        const string body = """
        {"status":"ok","data":[
          {"id_terminal":1,"id_commodity":10,"price_sell":100,"price_sell_avg_week":100,
           "game_version":"4.9","date_modified":99999999999999,"terminal_name":"Far Future"}
        ],"message":""}
        """;

        var ex = Record.Exception(() => MarketParse.ParsePriceRows(body, out _));

        Assert.Null(ex);
        var rows = MarketParse.ParsePriceRows(body, out var skipped);
        Assert.Empty(rows);
        Assert.Equal(1, skipped);
    }

    // Fix-round regression: a JSON number like 1e400 parses without throwing but lands as
    // double.PositiveInfinity; that must skip the row rather than produce a non-finite price.
    [Fact]
    public void ParsePriceRows_NonFiniteSellValue_SkipsRowNoThrow()
    {
        const string body = """
        {"status":"ok","data":[
          {"id_terminal":1,"id_commodity":10,"price_sell":1e400,"price_sell_avg_week":100,
           "game_version":"4.9","date_modified":1784939404,"terminal_name":"Overflow Price"}
        ],"message":""}
        """;

        var ex = Record.Exception(() => MarketParse.ParsePriceRows(body, out _));

        Assert.Null(ex);
        var rows = MarketParse.ParsePriceRows(body, out var skipped);
        Assert.Empty(rows);
        Assert.Equal(1, skipped);
    }

    // --- ParseLiveGameVersion -------------------------------------------

    [Fact]
    public void ParseLiveGameVersion_ReturnsLiveValue()
    {
        const string body = """{"status":"ok","data":{"live":"4.9","ptu":"4.10.0"}}""";
        Assert.Equal("4.9", MarketParse.ParseLiveGameVersion(body));
    }

    [Fact]
    public void ParseLiveGameVersion_Garbage_ReturnsNull()
    {
        Assert.Null(MarketParse.ParseLiveGameVersion("not json at all"));
        Assert.Null(MarketParse.ParseLiveGameVersion(""));
        Assert.Null(MarketParse.ParseLiveGameVersion(NotAllowedEnvelope));
    }

    // --- ParseTerminals ---------------------------------------------------

    [Fact]
    public void ParseTerminals_LocationCoalescesStationOverCityOverOutpost()
    {
        const string body = """
        {"status":"ok","data":[
          {"id":100,"name":"Terra Mills HUB","type":"trade","is_refinery":0,"star_system_name":"Stanton",
           "space_station_name":"Station A","city_name":"City A","outpost_name":"Outpost A"},
          {"id":101,"name":"ARC-L1","type":"refinery","is_refinery":1,"star_system_name":"Stanton",
           "space_station_name":"","city_name":"Lorville","outpost_name":"Some Outpost"},
          {"id":102,"name":"HDMS-Hadley","type":"refinery","is_refinery":1,"star_system_name":"microTech",
           "outpost_name":"HDMS-Hadley"},
          {"id":103,"name":"No Location","type":"trade","is_refinery":0,"star_system_name":"Stanton"}
        ],"message":""}
        """;

        var rows = MarketParse.ParseTerminals(body, out var skipped);

        Assert.Equal(0, skipped);
        Assert.Equal(4, rows.Count);
        Assert.Equal("Station A", rows[0].Location);
        Assert.False(rows[0].IsRefinery);
        Assert.Equal("Lorville", rows[1].Location);
        Assert.True(rows[1].IsRefinery);
        Assert.Equal("HDMS-Hadley", rows[2].Location);
        Assert.Equal("", rows[3].Location);
    }

    [Fact]
    public void ParseTerminals_MissingRequiredField_SkipsRow()
    {
        const string body = """
        {"status":"ok","data":[
          {"id":1,"type":"trade","is_refinery":0,"star_system_name":"Stanton"}
        ],"message":""}
        """;

        var rows = MarketParse.ParseTerminals(body, out var skipped);

        Assert.Empty(rows);
        Assert.Equal(1, skipped);
    }

    // --- ParseYieldRows -----------------------------------------------------

    [Fact]
    public void ParseYieldRows_HappyPath()
    {
        const string body = """
        {"status":"ok","data":[
          {"id_terminal":5,"id_commodity":10,"value":15,"value_week":12,
           "date_modified":1784939404,"terminal_name":"HDMS-Hadley"}
        ],"message":""}
        """;

        var rows = MarketParse.ParseYieldRows(body, out var skipped);

        Assert.Equal(0, skipped);
        var row = Assert.Single(rows);
        Assert.Equal(new MarketYieldRow(5, 10, 15, 12,
            new DateTime(2026, 7, 25, 0, 30, 4, DateTimeKind.Utc), "HDMS-Hadley"), row);
    }

    [Fact]
    public void ParseYieldRows_MissingValueWeek_SkipsRow()
    {
        const string body = """
        {"status":"ok","data":[
          {"id_terminal":5,"id_commodity":10,"value":15,
           "date_modified":1784939404,"terminal_name":"HDMS-Hadley"}
        ],"message":""}
        """;

        var rows = MarketParse.ParseYieldRows(body, out var skipped);

        Assert.Empty(rows);
        Assert.Equal(1, skipped);
    }

    // --- NewestFetchUtc -----------------------------------------------------

    [Fact]
    public void NewestFetchUtc_FreshSnapshot_IsMinValue()
    {
        var snap = new MarketSnapshot();
        Assert.Equal(DateTime.MinValue, snap.NewestFetchUtc);
    }

    [Fact]
    public void NewestFetchUtc_ReturnsMaxAcrossDatasets()
    {
        var t1 = new DateTime(2026, 7, 20, 0, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2026, 7, 25, 12, 0, 0, DateTimeKind.Utc);
        var t3 = new DateTime(2026, 7, 22, 0, 0, 0, DateTimeKind.Utc);
        var snap = new MarketSnapshot
        {
            Commodities = new MarketDataset<MarketCommodity> { FetchedUtc = t1 },
            RawPrices = new MarketDataset<MarketPriceRow> { FetchedUtc = t2 },
            RefinedPrices = new MarketDataset<MarketPriceRow> { FetchedUtc = t3 },
            Yields = new MarketDataset<MarketYieldRow>(),      // MinValue
            Terminals = new MarketDataset<MarketTerminal>(),   // MinValue
        };

        Assert.Equal(t2, snap.NewestFetchUtc);
    }
}
