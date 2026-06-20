# Blueprint Ownership Tracking — Design (V1)

**Date:** 2026-06-20
**Status:** Approved, pending implementation plan
**Branch:** `feature/blueprint-ownership`

## Problem

In Star Citizen, players unlock blueprints, but the only way to see which
blueprints you own is to travel to a specific in-game location. Nexus already
has a Blueprint Library (browse recipes, ingredients, unlock missions) but no
notion of which blueprints the user personally owns. Users want to track
ownership in Nexus so they don't have to check in-game.

## Goal

Let the user mark blueprints as owned, see an owned count, and filter the
Blueprint Library by ownership. Scope is V1: a per-blueprint owned flag plus a
filter — entirely inside the existing Blueprint Library page. No craftability
cross-referencing, no separate "My Blueprints" view (that was V2, deferred).

## Non-Goals

- Cross-referencing owned blueprints against refinery/inventory ("what can I
  craft") — explicitly out of scope.
- A dedicated top-level "My Blueprints" navigation view — deferred (V2).
- Importing/syncing ownership from the game — no game integration exists; the
  app is fully offline.

## Data & Persistence

- Add `public List<string> OwnedBlueprints { get; set; } = [];` to
  `AppSettings` (`NexusApp/Models/AppSettings.cs`).
- Persisted in `%AppData%\NexusApp\settings.json` via the existing
  `SettingsService` — the same pattern already used by `PinnedResources`.
- Ownership is keyed by blueprint **name** (the blueprint's existing identity,
  used everywhere else in the data layer). This deliberately decouples
  ownership from the SQLite `nexus.db`, which is reseeded from embedded
  `seed_data.json` on every app update — name-keyed state in `settings.json`
  survives those reseeds.
- No `SettingsSchemaVersion` bump needed: the field is additive and
  deserializes to an empty list for existing users' settings files.

## Components

### SettingsService (`NexusApp/Services/SettingsService.cs`)
- `bool IsBlueprintOwned(string name)` — membership check.
- `void SetBlueprintOwned(string name, bool owned)` — add/remove from the list
  and call `Save()` so the change is written to disk immediately.
- An owned-count accessor (e.g. `int OwnedBlueprintCount => Current.OwnedBlueprints.Count`).
- Use a case-stable comparison consistent with how blueprint names are matched
  elsewhere; store canonical names so the set has no duplicates.

### MainViewModel (`NexusApp/ViewModels/MainViewModel.cs`)
- `BlueprintFilter` state with three values: `All`, `Owned`, `NotOwned`.
- `OwnedBlueprintCount` observable property, surfaced for the count display.
- A toggle command/method that flips ownership via `SettingsService`, updates
  the count, and triggers a nav refresh when a filter is active.
- The existing blueprint search/build path honors `BlueprintFilter`.

### MainWindow (`NexusApp/Views/MainWindow.xaml` + `.xaml.cs`)
- The blueprint nav (`BlueprintNavPanel`) and detail (`BlueprintDetailPanel`)
  are built imperatively in code-behind, so the new controls are wired there:
  - **Nav row checkbox:** a clickable checkbox on the left of each blueprint
    row. Clicking it toggles ownership **in place** and must NOT trigger the
    row's drill-down navigation (handle/stop the click separately).
  - **Detail panel toggle:** an "Owned" toggle button reflecting current state,
    consistent with the luxury-gold theme.

## UI

- **Filter chip row + count** added to `PageBlueprints` in `MainWindow.xaml`,
  above the existing two-panel grid: chips `All · Owned · Not owned`, plus an
  "N owned" count.
- **Nav rows:** checkbox indicator (checked = owned), clickable to toggle.
- **Detail panel:** "Owned" toggle button.
- **Filtering:** when `Owned` or `Not owned` is active, the drill-down shows
  only matching blueprints and prunes parent category/subcategory groups that
  become empty. The filter combines with the existing text search.
- All new UI follows the luxury-gold theme (gold accent `#c9a227`-family,
  dark backgrounds), matching the approved mockup.

## Data Flow

1. User clicks a nav-row checkbox or the detail-panel "Owned" toggle.
2. `MainViewModel` calls `SettingsService.SetBlueprintOwned(name, newState)`,
   which mutates `OwnedBlueprints` and saves `settings.json` immediately.
3. `MainViewModel` updates `OwnedBlueprintCount`.
4. The affected row's check state and the detail toggle update; if a filter is
   active, the nav is re-filtered/rebuilt.

## Testing / Verification

WPF cannot be built in the WSL dev environment, so the verification gates are:

- **CI build-check** on push (compile gate) — primary automated gate.
- **Manual run on Windows** via `~/test_nexus.ps1`, verifying:
  - Marking a blueprint owned (from both nav checkbox and detail toggle)
    persists across an app restart.
  - Filter chips (All / Owned / Not owned) correctly show/hide blueprints and
    prune empty groups.
  - Owned count updates live as blueprints are toggled.
  - Ownership survives an app update (i.e. the `nexus.db` reseed) because it is
    stored name-keyed in `settings.json`.

During planning, check for an existing unit-test project. If one exists, the
owned-set logic in `SettingsService` (add/remove/contains/count) should be
factored to be unit-testable and covered there. If no test project exists, do
not introduce one solely for this feature — rely on the gates above.

## Rollout

Feature branch `feature/blueprint-ownership`. On completion, follow the normal
Nexus release flow (commit + push → CI build-check; cut a release via version
bump + git tag if/when shipping to users — ownership data is local, no OTA).
