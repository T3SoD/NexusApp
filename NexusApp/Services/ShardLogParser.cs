using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using NexusApp.Models;

namespace NexusApp.Services;

// Pure parsing of the Game.log "<Join PU>" shard line. No PII (server ip only).
public static class ShardLogParser
{
    private static readonly Regex Join = new(
        @"^<(?<ts>[0-9T:.Z+-]+)>.*?<Join PU> address\[(?<ip>[0-9.]+)\] port\[\d+\] " +
        @"shard\[(?<shard>(?:pub|priv)_(?<region>[a-z]+\d+[a-z])_\d+_(?<instance>\d+))\]",
        RegexOptions.Compiled);

    public static ShardSession? ParseJoin(string raw)
    {
        var m = Join.Match(raw);
        if (!m.Success) return null;
        var when = DateTimeOffset.TryParse(m.Groups["ts"].Value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto)
            ? dto.UtcDateTime : DateTime.UtcNow;
        var code = m.Groups["region"].Value;
        return new ShardSession
        {
            ShardId = m.Groups["shard"].Value,
            RegionCode = code,
            Region = DecodeRegion(code),
            Instance = m.Groups["instance"].Value,
            ServerIp = m.Groups["ip"].Value,
            JoinedAt = when,
        };
    }

    public static string DecodeRegion(string regionCode)
    {
        var prefix = new string(regionCode.TakeWhile(char.IsLetter).ToArray());
        return prefix switch
        {
            "use"  => "US East",
            "usw"  => "US West",
            "euw"  => "EU West",
            "euc"  => "EU Central",
            "eu"   => "EU",
            "apse" => "Asia SE",
            "apne" => "Asia NE",
            "ape"  => "Asia E",
            "aps"  => "Asia S",
            "aus"  => "Australia",
            "sae"  => "S. America",
            _      => regionCode.ToUpperInvariant(),
        };
    }
}
