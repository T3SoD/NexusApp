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

    // ── ToggleLabel / ShouldShow (USE WALLET became a toggle, 2026-08-10) ──────
    // It stopped being a one-shot push and became a switch that keeps the budget tracking the
    // wallet, which changes what the control must do while the estimate is briefly unusable.

    [Fact]
    public void ToggleLabel_CarriesTheFigureWhenUsable()
        => Assert.Equal("USE WALLET: 1,284,102", WalletBudgetChip.ToggleLabel(WalletUiState.Current, 1_284_102));

    // Never null, unlike Label: a toggle the user turned ON has to stay on screen and keep saying
    // so, or the control looks like it threw their choice away the moment the wallet went stale.
    [Theory]
    [InlineData(WalletUiState.NotSet)]
    [InlineData(WalletUiState.Impossible)]
    public void ToggleLabel_StillNamesItselfWhenUnusable(WalletUiState state)
        => Assert.Equal("USE WALLET", WalletBudgetChip.ToggleLabel(state, null));

    [Fact]
    public void ShouldShow_HiddenOnlyWhenBothOffAndUnusable()
    {
        Assert.False(WalletBudgetChip.ShouldShow(WalletUiState.NotSet, null, toggleOn: false));
        Assert.True(WalletBudgetChip.ShouldShow(WalletUiState.NotSet, null, toggleOn: true));
        Assert.True(WalletBudgetChip.ShouldShow(WalletUiState.Current, 1_284_102, toggleOn: false));
    }

    // ── Planner wiring pin ─────────────────────────────────────────────────

    [Fact]
    public void Planner_ReferencesTheChipAndSubscribesWalletChanged()
    {
        var src = SourceFiles.ReadAppSource(@"Views\TradePage.Planner.cs");
        Assert.Contains("WalletBudgetChip", src);
        Assert.Contains("App.Wallet.Changed", src);
    }

    // The toggle drives the budget, so every wallet raise is a chance to push a new figure in.
    [Fact]
    public void Planner_PushesTheWalletIntoTheBudgetWhileTheToggleIsOn()
    {
        var src = SourceFiles.ReadAppSource(@"Views\TradePage.Planner.cs");
        Assert.Contains("if (WalletBudgetOn) PushWalletIntoBudget", src);
    }

    // Two guards, both load-bearing. RebuildPlanner calls RefreshWalletChip, which calls back into
    // PushWalletIntoBudget: the unchanged-text check breaks that loop in the steady state, and
    // _inWalletPush covers the first push, where the text really is changing.
    [Fact]
    public void Planner_CannotLoopBetweenTheRebuildAndTheWalletPush()
    {
        var src = SourceFiles.ReadAppSource(@"Views\TradePage.Planner.cs");
        Assert.Contains("if (_inWalletPush) return;", src);
        Assert.Contains("if (string.Equals(_budgetBox.Text, text, StringComparison.Ordinal)) return;", src);
    }

    // Typing into a box the wallet is about to overwrite would be a lie, so the box locks while the
    // toggle is on.
    [Fact]
    public void Planner_LocksTheBudgetBoxWhileTheWalletDrivesIt()
    {
        var src = SourceFiles.ReadAppSource(@"Views\TradePage.Planner.cs");
        Assert.Contains("_budgetBox.IsReadOnly = WalletBudgetOn", src);
    }

    // ── Persistence (2026-08-10) ───────────────────────────────────────────

    // Off for every existing install, so an upgrade changes nothing until the user asks it to.
    [Fact]
    public void TheToggleDefaultsOff()
        => Assert.False(new NexusApp.Models.AppSettings().TradeBudgetFromWallet);

    // Read straight through AppSettings rather than cached in a field: one source of truth, and no
    // seeding step at construction that a later refactor could drop.
    [Fact]
    public void TheToggleIsBackedBySettings_NotAPageField()
    {
        var src = SourceFiles.ReadAppSource(@"Views\TradePage.Planner.cs");
        Assert.Contains("get => App.Settings.Current.TradeBudgetFromWallet;", src);
        Assert.Contains("App.Settings.Current.TradeBudgetFromWallet = value;", src);
        Assert.DoesNotContain("private bool _walletBudgetOn", src);
    }

    // A session can OPEN with the budget already driven by the wallet, so the read-only lock has to
    // be applied while the chrome is built. Without it the box starts editable and only locks on
    // the next toggle, which reads as the setting not having survived the restart.
    [Fact]
    public void ThePersistedToggle_LocksTheBoxOnStartup()
    {
        var src = SourceFiles.ReadAppSource(@"Views\TradePage.Planner.cs");
        var build = src.IndexOf("budgetGrp.Children.Add(budgetRow);", StringComparison.Ordinal);
        Assert.True(build > 0, "the budget row must still be assembled in the chrome build");
        Assert.Contains("ApplyWalletBudgetLock();", src[build..(build + 400)]);
    }

    // The budget VALUE stays session-only: planning against your wallet is a lasting preference,
    // while the figure itself only means anything for the session that measured it.
    [Fact]
    public void TheBudgetValueItselfIsStillNotPersisted()
        => Assert.DoesNotContain("TradeBudgetText", SourceFiles.ReadAppSource(@"Models\AppSettings.cs"));
}
