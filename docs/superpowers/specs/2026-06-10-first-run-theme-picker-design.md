# First-Run Theme Picker — Design

**Date:** 2026-06-10 (shipped in 5.1.0, 2026-06-11)
**Status:** Implemented
**Components:** `NexusApp/Views/ThemePickerWindow.cs`, `NexusApp/App.xaml.cs`

## Problem

The app silently defaulted to the Luxury Gold theme on first launch; new users never
saw the Classic option. Runtime theme switching is restart-based (the palette is applied
once at boot, before windows are built), so a naive picker would force a restart.

## Design

Show a "choose your look" picker **before the main window is built**, so the app opens
directly in the chosen theme with no restart.

- New `ThemePickerWindow` (code-built, self-contained colors so it renders the same
  regardless of which palette is loaded). Two selectable cards (Luxury Gold / Classic
  teal) with logo, swatches, and an accent-glow on the selected card; a Continue button.
  Returns `SelectedTheme` ("luxury" | "classic"); defaults to luxury if dismissed.
- In `App.OnStartup`, when `!Settings.Current.FirstRunComplete`: guard `ShutdownMode`
  (the picker auto-becomes `MainWindow`, so set `OnExplicitShutdown`, show it, apply the
  theme, then null `MainWindow` so `StartupUri` reassigns the real window). Reuses the
  existing `FirstRunComplete` gate, so the welcome tour still fires afterward in
  `MainWindow.Loaded`. Upgraders (flag force-set true by migration) skip the picker.

## Follow-up

Cards reserve a screenshot slot; upgrade to screenshot-hero once a Luxury screenshot is
captured on Windows (Classic screenshots already exist under `docs/screenshots/`).

## Testing

CI build-check compiles. Manual on Windows: delete `%APPDATA%\NexusApp\settings.json` to
force first-run; verify picker appears pre-window, classic gives teal app + tour, luxury
gives gold, dismiss-via-X defaults to luxury without crashing, existing settings skip the picker.
