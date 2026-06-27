using NexusApp.Models;
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

public class ShardLogParserTests
{
    private const string UseJoin =
        "<2026-06-27T13:14:51.882Z> [Notice] <Join PU> address[34.181.129.126] port[64318] " +
        "shard[pub_use1b_12030094_140] locationId[844429225164801] [Team_GameServices][GIM][Matchmaking]";

    private const string EuJoin =
        "<2026-06-27T08:01:00.000Z> [Notice] <Join PU> address[130.211.53.99] port[64311] " +
        "shard[pub_euw1b_11704877_070] locationId[844429225164801] [Team_GameServices][GIM][Matchmaking]";

    private const string CurlyJoin =
        "<2026-06-27T13:14:51.882Z> [+] [CIG] {Join PU} [0] id[600d1825-286f-4f61-8e9c-2732697b766b] status[1] port[64318]";

    [Fact]
    public void ParseJoin_ExtractsAllFields()
    {
        var s = ShardLogParser.ParseJoin(UseJoin);
        Assert.NotNull(s);
        Assert.Equal("pub_use1b_12030094_140", s!.ShardId);
        Assert.Equal("use1b", s.RegionCode);
        Assert.Equal("US East", s.Region);
        Assert.Equal("140", s.Instance);
        Assert.Equal("34.181.129.126", s.ServerIp);
        Assert.Equal(2026, s.JoinedAt.Year);
        Assert.Equal(DateTimeKind.Utc, s.JoinedAt.Kind);
    }

    [Fact]
    public void ParseJoin_DecodesEuRegion()
    {
        var s = ShardLogParser.ParseJoin(EuJoin);
        Assert.Equal("EU West", s!.Region);
        Assert.Equal("070", s.Instance);
    }

    [Fact]
    public void ParseJoin_CurlyVariant_ReturnsNull() => Assert.Null(ShardLogParser.ParseJoin(CurlyJoin));

    [Fact]
    public void ParseJoin_NonJoinLine_ReturnsNull() =>
        Assert.Null(ShardLogParser.ParseJoin("<2026-06-27T13:14:51.882Z> [Notice] <Something Else> foo"));

    [Theory]
    [InlineData("use1b", "US East")]
    [InlineData("usw2a", "US West")]
    [InlineData("euw1b", "EU West")]
    [InlineData("apse2a", "Asia SE")]
    [InlineData("ape1a", "Asia E")]
    [InlineData("aus1a", "Australia")]
    [InlineData("zzz9z", "ZZZ9Z")]
    public void DecodeRegion_MapsKnownPrefixes(string code, string expected) =>
        Assert.Equal(expected, ShardLogParser.DecodeRegion(code));
}
