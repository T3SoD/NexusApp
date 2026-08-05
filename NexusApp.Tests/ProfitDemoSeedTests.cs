using System.IO;
using NexusApp.Models;
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// Profit spec section 8: the demo profile seeds two commodity transactions (one buy, one sell)
// so public screenshots show a plausible, non-real ledger. Pinned against the embedded resource
// itself, through the real parser, so the seed cannot rot out of the three line shapes.
public class ProfitDemoSeedTests
{
    [Fact]
    public void DemoGameLog_SeedsOneSettledBuyAndOneSettledSell()
    {
        using var s = typeof(DataService).Assembly.GetManifestResourceStream("NexusApp.Data.demo.Game.log");
        Assert.NotNull(s);
        using var reader = new StreamReader(s!);

        var buys = new List<CommodityTransaction>();
        var sells = new List<CommodityTransaction>();
        int errors = 0;
        while (reader.ReadLine() is { } line)
        {
            if (!CommodityLogParser.LooksCommodityRelevant(line)) continue;
            if (CommodityLogParser.ParseBuy(line) is { } buyTx) buys.Add(buyTx);
            else if (CommodityLogParser.ParseSell(line) is { } sellTx) sells.Add(sellTx);
            else if (CommodityLogParser.ParseTransactionError(line) is not null) errors++;
        }

        var buy = Assert.Single(buys);
        var sell = Assert.Single(sells);
        Assert.Equal(0, errors);   // both seeds settle; the demo ledger shows no voided rows
        Assert.True(buy.Amount > 0);
        Assert.True(sell.Amount > buy.Amount);              // a plausible profitable round trip
        Assert.Equal(buy.ResourceGuid, sell.ResourceGuid);  // the same commodity out and back
        Assert.True(buy.TimestampUtc < sell.TimestampUtc);
    }
}
