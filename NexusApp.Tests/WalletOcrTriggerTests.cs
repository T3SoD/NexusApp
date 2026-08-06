using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

public class WalletOcrTriggerTests
{
    [Fact]
    public void MatchesTheVerbatimTriggerLine()
    {
        Assert.True(WalletOcrTrigger.IsMobiGlasOpenSignal(WalletLogFixtures.TriggerLine));
    }

    [Fact]
    public void RejectsEveryNonTriggerShape()
    {
        Assert.False(WalletOcrTrigger.IsMobiGlasOpenSignal(WalletLogFixtures.NoisyTwinLine));
        Assert.False(WalletOcrTrigger.IsMobiGlasOpenSignal(WalletLogFixtures.InventoryLine));
        Assert.False(WalletOcrTrigger.IsMobiGlasOpenSignal(CommodityLogFixtures.BuyLine));
        Assert.False(WalletOcrTrigger.IsMobiGlasOpenSignal(CommodityLogFixtures.SellLine));
        Assert.False(WalletOcrTrigger.IsMobiGlasOpenSignal(CommodityLogFixtures.ErrorLine));
        Assert.False(WalletOcrTrigger.IsMobiGlasOpenSignal(""));
    }

    [Fact]
    public void ParsesTheLineStampAsUtc()
    {
        Assert.True(WalletOcrTrigger.TryParseLineUtc(WalletLogFixtures.TriggerLine, out var utc));
        Assert.Equal(new DateTime(2026, 8, 6, 0, 26, 37, 290, DateTimeKind.Utc), utc);
        Assert.Equal(DateTimeKind.Utc, utc.Kind);
    }

    [Theory]
    [InlineData("")]
    [InlineData("no stamp at all")]
    [InlineData("<not-a-stamp-but-26-chars> [Notice] something")]
    [InlineData("[Notice] <VehicleListQuery> stamp missing entirely")]
    public void RejectsMalformedStamps(string raw)
    {
        Assert.False(WalletOcrTrigger.TryParseLineUtc(raw, out _));
    }

    [Theory]
    [InlineData("5,230,346", 5230346L)]
    [InlineData("Balance 5,230,346 aUEC", 5230346L)]
    [InlineData("5.230.346", 5230346L)]
    [InlineData("REC 12 balance 5,230,346", 5230346L)]
    [InlineData("0", 0L)]
    [InlineData("846", 846L)]
    public void ExtractsThePlausibleBalance(string ocrText, long expected)
    {
        Assert.Equal(expected, WalletOcrTrigger.ExtractBalance(ocrText));
    }

    [Theory]
    [InlineData("")]
    [InlineData("aUEC")]
    [InlineData("no digits here")]
    [InlineData("123456789012")] // 12 digits, above the plausibility bound
    public void RefusesImplausibleText(string ocrText)
    {
        Assert.Null(WalletOcrTrigger.ExtractBalance(ocrText));
    }

    [Fact]
    public void MostDigitsWinsOverEarlierShorterGroups()
    {
        Assert.Equal(1067200L, WalletOcrTrigger.ExtractBalance("14:02 1,067,200 aUEC"));
    }
}
