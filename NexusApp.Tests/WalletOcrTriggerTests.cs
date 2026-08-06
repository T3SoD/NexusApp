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

    // Dual-recognition verdict per grab (calibration round 3, 2026-08-06 16:07 live evidence:
    // the plain pass fails routinely against the animated mobiGlas background, so requiring
    // both passes rejected 11 of 12 real grabs). Agreement is preferred, a genuine parse
    // DISAGREEMENT rejects the grab (that is the misread signal), and a single reading pass
    // stands alone because the burst's cross-grab agreement still guards it.
    [Theory]
    [InlineData("5,105,256", "5105256", "5,105,256")]  // both agree: keep the first pass's text
    [InlineData("5,101,948 I.", "", "5,101,948 I.")]   // only one pass reads: it stands
    [InlineData(null, "846", "846")]
    public void BestReadPrefersAgreementThenTheOnlyReadingPass(string? a, string? b, string expected)
    {
        Assert.Equal(expected, WalletOcrTrigger.BestRead(a, b));
    }

    [Theory]
    [InlineData("5,105,256", "5,105,252")] // both parse, values differ: the true misread signal
    [InlineData("", null)]
    [InlineData("no digits", "none here")]
    public void BestReadRejectsDisagreementAndSilence(string? a, string? b)
    {
        Assert.Null(WalletOcrTrigger.BestRead(a, b));
    }

    [Fact]
    public void MostDigitsWinsOverEarlierShorterGroups()
    {
        Assert.Equal(1067200L, WalletOcrTrigger.ExtractBalance("14:02 1,067,200 aUEC"));
    }

    // The wallet region can catch the player handle under the balance, and handles may embed
    // digits (leetspeak). Live calibration 2026-08-06: a real capture read the handle line while
    // missing the number entirely, so letter-flanked digits must never become a balance.
    [Theory]
    [InlineData("PL4Y3RNAME")]
    [InlineData("H4ULER")]
    public void LetterFlankedDigitsAreNeverABalance(string ocrText)
    {
        Assert.Null(WalletOcrTrigger.ExtractBalance(ocrText));
    }

    // Live failure 2026-08-06 16:44: OCR read the trailing 8 as the letter B ("5,101.94B") and
    // the letter-flank rule backtracked into accepting the truncated "5,101". A digit run that
    // touches a letter is a misread; it must be discarded WHOLE, never shortened.
    [Theory]
    [InlineData("5,101.94B")]
    [InlineData("Ä 5,101,94B")]
    [InlineData("123,456X 78")] // even when a shorter clean token (78) exists, the poisoned run never yields "123,456"
    public void LetterTouchedRunsAreDiscardedWholeNeverTruncated(string ocrText)
    {
        var result = WalletOcrTrigger.ExtractBalance(ocrText);
        Assert.True(result is null or 78, $"got {result}");
        Assert.NotEqual(5101L, result);
        Assert.NotEqual(123456L, result);
    }

    [Fact]
    public void StandaloneNumberStillWinsBesideALeetHandle()
    {
        Assert.Equal(5105256L, WalletOcrTrigger.ExtractBalance("5105256 PL4Y3RNAME"));
        Assert.Equal(846L, WalletOcrTrigger.ExtractBalance("H4ULER 846"));
    }

    [Fact]
    public void ParsesAContractCompletionNameAndStamp()
    {
        Assert.True(WalletOcrTrigger.TryParseContractComplete(
            WalletLogFixtures.ContractCompleteLine, out var name, out var utc));
        Assert.Equal("Rookie Rank - Direct Medium Cargo Haul", name);
        Assert.Equal(new DateTime(2026, 8, 6, 0, 28, 10, 500, DateTimeKind.Utc), utc);
    }

    [Fact]
    public void StripsMetaMarkupAndKeepsColonsInsideTheName()
    {
        Assert.True(WalletOcrTrigger.TryParseContractComplete(
            WalletLogFixtures.ContractCompleteMarkupLine, out var name, out _));
        Assert.Equal("Jorrit Dossier: Project Hyperion", name);
    }

    [Fact]
    public void AnAcceptanceNotificationIsNotACompletion()
    {
        Assert.False(WalletOcrTrigger.TryParseContractComplete(
            WalletLogFixtures.ContractAcceptedLine, out _, out _));
    }

    // Recon hazard: notification text can span lines ("You sent :\n1306500 aUEC"), so a line
    // that carries the marker but not the closing sequence must be rejected, never guessed at.
    [Fact]
    public void ATruncatedNotificationLineIsRejected()
    {
        var closeAt = WalletLogFixtures.ContractCompleteLine.IndexOf(": \" [", StringComparison.Ordinal);
        var truncated = WalletLogFixtures.ContractCompleteLine.Substring(0, closeAt);
        Assert.False(WalletOcrTrigger.TryParseContractComplete(truncated, out _, out _));
    }
}
