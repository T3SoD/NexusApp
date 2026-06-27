# Cargo Contract OCR - Design

**Status:** approved in brainstorming 2026-06-27. Branch: `feature/cargo-hauling`.

## Goal

Auto-import full hauling-contract details from the in-game Star Citizen **Contracts** panel and attach them to the matching Game.log-tracked haul. The Game.log gives mission lifecycle (accepted / leg-complete / ended) and a missionId, but lacks the aUEC reward, the contractor name, and - for the CFP/`_Hauling` family - the commodity / SCU / pickup source entirely. The on-screen Contracts panel has all of it. OCR fills the gaps.

## What the panel shows (from real screenshots, build 12061511)

The Contracts > Accepted detail panel (right side) contains, per contract:
- **Title** heading, e.g. `Opportunity for Independent Cargo Hauler | Patch City > Checkmate [100 Rep]`, `Need a Hauler [50/200 Rep]`.
- **Reward** `¤ 119,000` / `169,250` / `139,250` (aUEC).
- **Contracted By** `Covalex Independent Contractors` / `Ling Family Hauling` / `Citizens For Prosperity`.
- **PRIMARY OBJECTIVES**, one or more, each a Deliver + a Collect:
  - `Deliver 0/6 SCU of Medical Supplies to Checkmate at the L4 Lagrange of Pyro II.` + `Collect Medical Supplies from Patch City.`
  - multi-commodity (one contract): `Deliver 0/4 SCU of Sunset Berries to Rayari Kaltag Research Outpost on Calliope.` + `Collect Sunset Berries from Chawla's Beach.` AND `Deliver 0/2 SCU of Golden Medmon ...` + `Collect Golden Medmon from Chawla's Beach.`
  - CFP: `Deliver 0/6 SCU of Hydrogen to Ruin Station above Pyro VI.` + `Collect Hydrogen from Jackson's Swap.`

The left list (all accepted) shows title + reward + company; v1 does NOT OCR the list - browsing the detail panel covers every accepted contract one at a time.

## Decisions (locked)

1. **Merge model: enrich the matching haul by title.** OCR details attach to the haul the log already created (matched by normalized title). One unified haul list.
2. **Trigger: one-time region + auto-scan.** The user draws a contract-panel region once (like the RS scan region); the app then auto-watches that region and imports each contract as it is shown. Deduped by title.
3. **Reconciliation (v1):** show the OCR objective list (rich: commodity / SCU / pickup / dropoff) plus the **overall** mission status from the log (Active / Complete / Abandoned). Per-objective "[done]" matching between OCR objectives and log legs is **deferred to v2** (no shared key; CFP log legs have no commodity).
4. **The locked RS `OcrService` (invert/contrast/scale/capture/ExtractRsValue) is NOT modified.** Contract OCR is a separate path. [[feedback-autoscan-image-processing]]
5. **PII:** the region is the detail panel; the parser extracts only title/reward/contractor/objectives. The player handle is never read or stored. (Handle must never appear in code/fixtures/commits. [[reference-github-repo]])

## Architecture

All new types; nothing in the locked RS scan path changes.

### `ContractOcrService` (Services)
- Owns its own `Windows.Media.Ocr.OcrEngine` (created the same way as `OcrService`: `TryCreateFromUserProfileLanguages()` then `en-US` fallback) and its own GDI BitBlt region capture (a small self-contained copy of the capture P/Invoke - duplicated, not shared, so the locked `OcrService` is untouched).
- **Preprocessing tuned for a text panel, not the RS digit:** invert + contrast (the panel is the same dark-navy/light-text as the RS box) but a lighter ~2x scale (the panel text is already large; the RS path's 6x would make a multi-megapixel bitmap and be slow). No magenta-boundary logic.
- API: `Task<string?> ScanRegionTextAsync(int x, int y, int w, int h)` - capture the region, preprocess, `RecognizeAsync`, return `OcrResult.Text` (full multi-line text) or null if the engine is unavailable.

### `ContractParser` (Services, pure static - TDD core)
- `static ContractDetails? Parse(string ocrText)` - returns null when no contract anchor is found (this null is ALSO how the scanner knows no contract is on screen). Tolerant of OCR noise.
- Extraction rules (anchored, order-independent):
  - **Reward:** the comma-grouped number near the `Reward` anchor / following the `¤` glyph; strip commas to an int. (5-6 digits.)
  - **Contracted By:** the text after the `Contracted By` anchor on its line.
  - **Title:** the heading text above `DETAILS` / the `Reward` block (the prominent top line(s)).
  - **Objectives:** each `Deliver \d+/(?<scu>\d+) SCU of (?<commodity>...) to (?<dropoff>...)` paired with its `Collect (?<commodity>...) from (?<pickup>...)` by matching commodity. Yields `ContractObjective { Commodity, Scu, Pickup, Dropoff }`. Supports multiple objectives per contract.
- `ContractDetails { string Title; int Reward; string ContractedBy; List<ContractObjective> Objectives; }`.

### `ContractScanner` (Services)
- A slow poll (~1s) over the saved contract region, gated by an `AutoScanContracts` toggle. Mirrors `ScannerService`'s timer pattern but is a separate instance so it never contends with the RS scanner.
- Each tick: `ContractOcrService.ScanRegionTextAsync(region)` -> `ContractParser.Parse(text)`. If a `ContractDetails` is returned whose normalized title differs from the last imported title, raise `event Action<ContractDetails> ContractScanned` and remember the title (dedup).
- `[CONTRACT]` log tag on each import (App Log Monitor visibility, definition-of-done). [[feedback-feature-logging]]

### Enrichment (wired in App, like the other trackers)
- On `ContractScanned(d)`: find the active haul whose normalized title matches `d.Title`; set `Haul.Reward`, `Haul.ContractedBy`, `Haul.ContractObjectives`; raise the haul tracker's `Changed`.
- No match yet (log lag / not-yet-accepted): stash `d` by normalized title in a small pending map; apply it when a haul with that title is next created.
- **Title normalization:** lowercase, strip the `[... Rep]` reward-tag suffix, collapse whitespace, drop non-alphanumerics; compare for equality (fuzzy-tolerant of OCR slips). The log haul title comes from `Contract Accepted` (`Haul.RouteTitle`); the OCR title from the panel heading - they normalize to the same string.

### Model additions (`Haul`)
- `int Reward` (aUEC, 0 = unknown), `string ContractedBy`, `List<ContractObjective> ContractObjectives` (OCR-sourced; empty until scanned).

### Persistence / settings
- `AppSettings.ContractRegion` (`ScanRegion?`) - the saved draw region.
- `AppSettings.AutoScanContracts` (bool) - the toggle state, restored on launch.

## UI

- **Controls** (overlay, next to the existing scan management): a `Set contract region` button (reuses `RegionSelectorWindow`, saves to `AppSettings.ContractRegion`) and an `Auto-scan contracts` on/off toggle (drives `ContractScanner`).
- **Display:** the main-window Hauling tab and overlay HAULING tab show, when present, the `Reward` (aUEC) and `ContractedBy`, and render the OCR `ContractObjectives` (commodity / SCU / `pickup -> dropoff`) as the haul's detail, with the overall log status (Active / Complete / Abandoned). Falls back to the log legs when no OCR objectives exist.

## Testing

- **`ContractParser`** is the TDD core: feed representative (noisy) OCR text built from the three screenshots; assert Title/Reward/ContractedBy and the objective list (incl. the multi-commodity case). Includes a "not a contract" text -> null case (the scanner's screen-detection).
- **Title normalization + enrichment matching** are unit-testable (string in, match out).
- `ContractOcrService` / `ContractScanner` are WPF + WinRT-OCR dependent: compile-verified on Windows/CI and runtime-verified by Zach (cannot build in WSL).

## Out of scope (v2+)

- Per-objective "[done]" matching between OCR objectives and log legs.
- OCR'ing the left accepted-list for an all-at-once import.
- Profit/earnings analytics off the reward (the data will exist once rewards are captured).
- Full-screen auto-locate (no region).

## PII / constraints recap

No emojis, no em-dashes, no Claude attribution. Handle never in code/fixtures/commits. Locked RS `OcrService` untouched. Cannot build/test WPF in WSL - parser tests run on Windows/CI; runtime verified by Zach.
