# Welcome Tour Redesign — Anchored Coach-Marks

**Date:** 2026-06-11
**Status:** Implemented
**Components:** `NexusApp/Views/CoachMarkWindow.cs`, `NexusApp/Views/TourController.cs`,
`NexusApp/Views/MainWindow.xaml.cs` (wiring); replaced `WelcomeWizardWindow.cs` (deleted).
Ring (`HighlightWindow.cs`) kept as-is.

## Problem

The old tour was a 560×690 modal locked to screen-center. The highlight ring pointed at
controls all over the app, but the card never moved — so on many steps the card covered
the very control it explained. It was also 14 long steps, fully modal, and used emojis in
step icons (against the no-emoji rule).

## Design

Replace the modal wizard with **anchored coach-marks**: a compact, modeless caption bubble
that docks beside each highlighted control and never covers it.

- `CoachMarkWindow` — topmost, no-activate (`WS_EX_NOACTIVATE`) bubble with title, caption,
  progress (Step X of N + dots), Skip/Back/Next, and a beak pointing at the target. Picks the
  first side with room (right → left → below → above), clamps to the work area. Theme-aware
  (gold in Luxury, teal in Classic). Centered mode for anchorless steps.
- `TourController` — owns the lean **8-step** script and the `TutorialTarget` enum, drives
  per-step navigation, and positions ring + bubble together. `MainWindow.ResolveTutorialTarget`
  navigates to the page/overlay and returns the element to anchor on.
- The final step's "Set up auto-scan" opens the region selector (preserved from the old flow).

Steps: Welcome · RS Decoder · Open overlay · Draw region · Start scanning · Overlay tabs ·
Reference tools (nav rail) · You're set.

## Testing

CI build-check compiles. Manual on Windows via `test_nexus.ps1` → Help (?) → Replay Tutorial:
bubble docks clear of the ring with the beak pointing at it; Back/Next/Skip respond (no-activate
window); centered steps center on the main window; both themes.
