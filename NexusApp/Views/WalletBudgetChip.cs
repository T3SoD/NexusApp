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

    // The TOGGLE's label (2026-08-10). USE WALLET stopped being a one-shot push and became a switch
    // that keeps the budget tracking the wallet, so unlike Label above this never returns null: a
    // toggle the user has turned ON must stay on screen and keep saying so even while the estimate
    // is briefly unusable, or the control appears to have thrown their choice away.
    public static string ToggleLabel(WalletUiState state, long? estimate) =>
        CanUse(state, estimate) ? $"USE WALLET: {ProfitDisplay.Format(estimate!.Value)}" : "USE WALLET";

    // Hidden only when it is BOTH off and unusable, which is the pre-toggle rule unchanged. On and
    // unusable still shows, per ToggleLabel's reasoning.
    public static bool ShouldShow(WalletUiState state, long? estimate, bool toggleOn) =>
        toggleOn || CanUse(state, estimate);

    // The digits the budget box receives, or null. Must round-trip through CurrentBudget(): that
    // parser strips everything but digits before parsing, so the comma-grouped ProfitDisplay
    // format is safe to hand it directly.
    public static string? BudgetText(WalletUiState state, long? estimate) =>
        CanUse(state, estimate) ? ProfitDisplay.Format(estimate!.Value) : null;
}
