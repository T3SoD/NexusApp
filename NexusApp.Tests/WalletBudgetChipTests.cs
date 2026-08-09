using System.Globalization;
using System.Linq;
using NexusApp.Views;
using Xunit;

namespace NexusApp.Tests;

// WalletBudgetChip: the pure fold behind the "use wallet as budget" chip (desktop planner) and
// the WALLET budget preset (overlay). CanUse's usability rule and the two renderings derived from
// it (Label, BudgetText) are the whole testable core; the WPF wiring on both surfaces is source-
// pinned below instead (SourceFiles.ReadAppSource idiom, same as OverlayClearHistoryTests).
public class WalletBudgetChipTests
{
    // ── CanUse ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(WalletUiState.Current)]
    [InlineData(WalletUiState.Aging)]
    [InlineData(WalletUiState.Offline)]
    public void CanUse_TrueForUsableStatesWithAPositiveEstimate(WalletUiState state)
        => Assert.True(WalletBudgetChip.CanUse(state, 1_284_102));

    [Fact]
    public void CanUse_FalseForNotSet()
        => Assert.False(WalletBudgetChip.CanUse(WalletUiState.NotSet, 1_284_102));

    [Fact]
    public void CanUse_FalseForImpossible()
        => Assert.False(WalletBudgetChip.CanUse(WalletUiState.Impossible, 1_284_102));

    [Theory]
    [InlineData(WalletUiState.Current)]
    [InlineData(WalletUiState.Aging)]
    [InlineData(WalletUiState.Offline)]
    public void CanUse_FalseWithNoEstimate(WalletUiState state)
        => Assert.False(WalletBudgetChip.CanUse(state, null));

    [Fact]
    public void CanUse_FalseForZero()
        => Assert.False(WalletBudgetChip.CanUse(WalletUiState.Current, 0));

    [Fact]
    public void CanUse_FalseForNegative()
        => Assert.False(WalletBudgetChip.CanUse(WalletUiState.Current, -5));

    // ── Label ──────────────────────────────────────────────────────────────

    [Fact]
    public void Label_FormatsWithGrouping()
        => Assert.Equal("USE WALLET: 1,284,102", WalletBudgetChip.Label(WalletUiState.Current, 1_284_102));

    [Fact]
    public void Label_NullWhenUnusable()
    {
        Assert.Null(WalletBudgetChip.Label(WalletUiState.NotSet, 1_284_102));
        Assert.Null(WalletBudgetChip.Label(WalletUiState.Impossible, 1_284_102));
        Assert.Null(WalletBudgetChip.Label(WalletUiState.Current, null));
        Assert.Null(WalletBudgetChip.Label(WalletUiState.Current, 0));
        Assert.Null(WalletBudgetChip.Label(WalletUiState.Current, -5));
    }

    // ── BudgetText ─────────────────────────────────────────────────────────

    [Fact]
    public void BudgetText_NullWhenUnusable()
    {
        Assert.Null(WalletBudgetChip.BudgetText(WalletUiState.NotSet, 1_284_102));
        Assert.Null(WalletBudgetChip.BudgetText(WalletUiState.Impossible, 1_284_102));
        Assert.Null(WalletBudgetChip.BudgetText(WalletUiState.Offline, null));
    }

    // Mirrors TradePage.Planner.cs's CurrentBudget() exactly: digits only, then
    // double.TryParse(NumberStyles.None, InvariantCulture) - so a comma-grouped BudgetText must
    // survive that round trip and land back on the original number.
    [Theory]
    [InlineData(1_284_102)]
    [InlineData(500_000)]
    [InlineData(1)]
    [InlineData(999_999_999)]
    public void BudgetText_RoundTripsThroughTheBudgetBoxParse(long estimate)
    {
        var text = WalletBudgetChip.BudgetText(WalletUiState.Current, estimate);
        Assert.NotNull(text);

        var digits = new string(text!.Where(char.IsDigit).ToArray());
        Assert.True(double.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed));
        Assert.Equal((double)estimate, parsed);
    }

    // ── Overlay wiring pin (SourceFiles.ReadAppSource idiom, OverlayClearHistoryTests) ────────

    [Fact]
    public void Overlay_WiresTheWalletBudgetPreset()
    {
        var src = SourceFiles.ReadAppSource(@"Views\OverlayWindow.xaml.cs");
        Assert.Contains("WalletBudgetChip", src);
        Assert.Contains("\"WALLET\"", src);
    }

    // ── Planner wiring pin ─────────────────────────────────────────────────

    [Fact]
    public void Planner_ReferencesTheChipAndSubscribesWalletChanged()
    {
        var src = SourceFiles.ReadAppSource(@"Views\TradePage.Planner.cs");
        Assert.Contains("WalletBudgetChip", src);
        Assert.Contains("App.Wallet.Changed", src);
    }
}
