namespace NexusApp.Views;

// "Use wallet as budget" pure fold, shared by the desktop planner chip (TradePage.Planner.cs) and
// the overlay's WALLET budget preset (OverlayWindow.xaml.cs). Both surfaces read the live wallet
// estimate and WalletDisplay.State themselves and pass the results in here - this class owns none
// of that state, only the usability rule and the two renderings derived from it.
internal static class WalletBudgetChip
{
    // Whether the wallet estimate is usable as a budget. NotSet has no number and Impossible is
    // negative; Aging and Offline are staleness signals, not wrongness, so they still fill.
    public static bool CanUse(WalletUiState state, long? estimate) =>
        estimate is { } e && e > 0 &&
        (state == WalletUiState.Current || state == WalletUiState.Aging || state == WalletUiState.Offline);

    // "USE WALLET: 1,284,102", or null when CanUse is false.
    public static string? Label(WalletUiState state, long? estimate) =>
        CanUse(state, estimate) ? $"USE WALLET: {ProfitDisplay.Format(estimate!.Value)}" : null;

    // The digits the budget box receives, or null. Must round-trip through CurrentBudget(): that
    // parser strips everything but digits before parsing, so the comma-grouped ProfitDisplay
    // format is safe to hand it directly.
    public static string? BudgetText(WalletUiState state, long? estimate) =>
        CanUse(state, estimate) ? ProfitDisplay.Format(estimate!.Value) : null;
}
