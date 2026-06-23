# Custom Component String Resolution — Design

**Date:** 2026-06-23
**Branch:** `feature/custom-component-strings`
**Issue:** #1 — "Reddit Feedback - Blueprint Import"
**Status:** Approved design, pending implementation plan

## Problem

Players use community localization mods (StarStrings, WP-StarStrings, and others) that rename
Star Citizen ship components to user-defined display strings. The mod rewrites the in-game display
name, so SC's `Game.log` "Received Blueprint" notification records the **custom** display string,
not the vanilla name.

Nexus's importer (`GameLogBlueprintImporter`) only understands two layouts:
1. Vanilla names (exact match).
2. The **default** StarStrings prefix `Class/Size/Grade Name` (e.g. `Mil/1/D Tundra`),
   which it strips before matching.

Any other custom format imports as **unrecognized**. From issue #1, 9 component blueprints came
through unmatched, e.g. `Q IND 3B Agni`, `R IND 3C Surveyor-Max`, `2AFR-76`.

Crucially, the format is **fully user-configurable** (the WP-StarStrings builder lets users compose
Type / Classification / Size / Grade / Name chips with any separator — or none). A real user file
(`global2.ini`) produces values like `2AFR-76`, `1DTundra`, `3BRanger` with **no separator at all**,
so heuristic prefix-stripping cannot work in general.

## Key insight (validated against real data)

Every localization mod edits **only the value side** of the game's localization file,
`…\StarCitizen\LIVE\Data\Localization\english\global.ini`. The **key** (the internal item code) is
constant across all mods and formats.

Verified by diffing two real user files (`global1.ini` default format vs `global2.ini` custom
format): both contain the **identical 9,171 `item_Name` keys**; only the values differ.

| internal key (constant) | global1 value | global2 value |
|---|---|---|
| `item_Name_SHLD_GODI_S02_FR76` | `Mil/2/A FR-76` | `2AFR-76` |
| `item_NameCOOL_AEGS_S01_Tundra` | `Mil/1/D Tundra` | `1DTundra` |
| `item_NameQDRV_TARS_S03_Ranger` | `Civ/3/B Ranger` | `3BRanger` |

Therefore the key is a **universal, mod-independent anchor**. If we read the user's own
`global.ini`, we can translate *their* custom display string back to the internal key, regardless
of the format they chose.

Two correctness facts that shape the design:
- The key is an internal **code**, not the clean display name. It embeds e.g. `FR76` (no hyphen),
  while the seed stores `FR-76`. Verified: `"FR76"` is **not** in the seed, `"FR-76"` is. So the
  key alone cannot match the seed — we need one more hop, `key → official name`.
- The key does not always contain the model name at all (quantum drives:
  `item_NameJUMP_TARS_S1_C = … Explorer`). So we cannot parse the official name out of the key.

## Resolution chain

```
Game.log display string
  → user's global.ini      (value → internal key)        [runtime, per user]
  → bundled reference      (internal key → official name) [shipped in-app]
  → existing seed match    (official name → blueprint)
  → mark owned
```

The join is the internal key, so this works for **any** mod or custom format — including formats
with no separators.

## Scope

**This branch: components only** (matches issue #1 and the branch name). The architecture
generalizes to weapons / armor / ships later by swapping the reference source for a full
default/vanilla `global.ini` — no redesign required.

## Components of the design

### 1. Bundled reference: internal key → official name

- **Source:** the community StarStrings `components.ini` (the canonical default component list,
  ~23 KB, maintained per patch in its public repo). It maps every component key to its
  **default-format** value `Class/Size/Grade Name`. (Provenance is tracked outside the codebase;
  no upstream author name appears in any shipped code, comment, identifier, or data file.)
- **Derivation:** strip the fixed default prefix from each value to recover the clean official
  name. Reuse the existing `GameLogBlueprintImporter.StarStringsPrefix` regex
  (`^[A-Za-z]+/\d+/[A-Za-z]+\s+`). Examples:
  - `item_Name_SHLD_GODI_S02_FR76 = Mil/2/A FR-76` → `FR-76`
  - `item_NameCOOL_AEGS_S01_Tundra = Mil/1/D Tundra` → `Tundra`
  - `item_NameJUMP_TARS_S1_C = Civ/1/C Explorer` → `Explorer`
  - If a value has no recognizable prefix, use it verbatim.
- **Packaging:** ship `components.ini` as an embedded resource and build the
  `key → officialName` dictionary once at startup. Per-patch refresh = drop in the updated
  `components.ini` (no separate generation step). This mirrors the existing seed-embedding model:
  the data only reaches users via a new build (no OTA — consistent with the app's offline design).
- **No vanilla `global.ini` required.**

### 2. `GlobalIniReader` service (runtime)

A new service that reads the user's `global.ini` and produces a `customDisplay → internalKey`
lookup.

- Parses line-by-line; keeps only lines whose key starts with `item_Name` (skips the large
  `item_Desc*` blocks and all non-item strings).
- Handles the UTF-8 BOM on the first line.
- **Key normalization:** both files contain mixed `item_Name…` and `item_Name_…` forms for the
  same item. Normalize keys (strip an optional `_` immediately after `item_Name`) on **both** the
  reference and the user file before joining, so the two always line up.
- Pure parse function takes an `IEnumerable<string>` (unit-testable headless); a thin IO wrapper
  opens the file shared/read-only (same pattern as `UnrecognizedBlueprintReport.TryReadBuildLine`).
- The full file is ~3–4 MB / ~89 k lines. Parse once per scan on the existing background thread;
  cache by path + last-write-time to avoid re-parsing within a session.

The reader then composes with the bundled reference to yield a single
`customDisplay → officialName` map: for each `item_Name` line, `value → key → officialName`.

### 3. Importer integration

Extend `GameLogBlueprintImporter` resolution with a third, optional step, tried only when steps
1–2 fail:

1. exact match (vanilla + untouched names)
2. strip default StarStrings prefix, retry (existing behavior)
3. **`customDisplay → officialName` via the user's global.ini map, then `Resolve(officialName)`** (new)

- The map is passed **per scan** as an optional `ScanHistory` parameter (e.g.
  `IReadOnlyDictionary<string,string>? localizationMap`), not via the constructor — the importer is
  a cached singleton (`GameLogSession.Importer`) but the user's `global.ini` can change between
  scans. `GameLogSession.ScanHistory` builds the map from the resolved `global.ini` path
  immediately before each scan and passes it in. `Resolve` consults it in step 3. This keeps the
  importer pure and headlessly testable.
- Names resolved this way join `HistoryScan.Matched` and flow through the existing
  `App.Settings.SetBlueprintsOwned(...)` path unchanged.
- Genuinely-unseeded names (e.g. `S CIV 3B 6CA 'Bila'`, where `Bila` is not in the seed) remain
  in `Unmatched` — the correct outcome.

### 4. Path detection + setting

- **Primary auto-detect:** derive from the already-configured Game.log path. Game.log lives at
  `…\StarCitizen\LIVE\Game.log`; global.ini at
  `…\StarCitizen\LIVE\Data\Localization\english\global.ini`. So
  `globalIni = <dir of Game.log>\Data\Localization\english\global.ini`. Zero extra user setup for
  anyone who already set their Game.log path.
- **Override:** add `AppSettings.GlobalIniPath` (nullable/empty default) plus a file picker in the
  Game.log Monitor / Settings, reusing the existing `GameLogPath` UI pattern, for non-standard
  installs or PTU/EPTU.
- If the file is absent or unreadable, the feature silently no-ops and import behaves exactly as
  today.

### 5. Logging

Wire a new `[LOC]` tag through `Logger` (per the feature-logging definition-of-done) so it surfaces
in the App Log Monitor + diagnostic snapshot:
- global.ini located / not found (path only — never contents; paths contain the Windows username,
  so log only "found"/"not found" and entry count, not the path).
- parsed entry count.
- count resolved via localization during a scan.

### 6. Safety / disclosure

- Strictly **read-only**; no game files modified. Consistent with the existing read-only Game.log
  stance.
- Reading `global.ini` is **CIG-sanctioned** community localization (per CIG's 2023 Community
  Localization statement).
- Add one line to About → Legal noting Nexus reads `global.ini` read-only to translate
  mod-renamed blueprint names.

## Testing

Unit-testable headless (no WPF). Use trimmed fixtures extracted from the real `global1.ini` /
`global2.ini` (a dozen representative components across categories + both key-form variants +
a quantum drive + a no-separator format) — not the full 4 MB files.

- **Reference builder:** `components.ini` value → official name strips correctly
  (`Mil/2/A FR-76 → FR-76`, `Civ/1/C Explorer → Explorer`); no-prefix values pass through.
- **GlobalIniReader:** parses `item_Name` lines into `value → key`; skips `item_Desc` and
  non-item lines; handles BOM; normalizes `item_Name` vs `item_Name_`.
- **End-to-end resolve (default format):** `Mil/2/A FR-76 → FR-76 →` seed match.
- **End-to-end resolve (custom, no separator):** `2AFR-76 → FR-76 →` seed match.
- **Quantum drive (name absent from key):** `3BRanger → Ranger →` seed match.
- **Genuinely unseeded:** `'Bila'`-style name stays in `Unmatched`.
- **Missing/unreadable global.ini:** no exception; results identical to today.
- **Key collision:** two keys with the same display value both resolve to the same official name
  (harmless).

## Edge cases & non-goals

- Non-component blueprints (weapons/armor/ships) are out of scope this branch; their custom names
  remain unrecognized until the reference is expanded to a full global.ini.
- PTU/EPTU and non-default install drives are handled via the override path.
- The reference is only as current as the bundled `components.ini`; stale data shows as
  unrecognized, never as a wrong match.

## Files (anticipated)

- `NexusApp/Services/GlobalIniReader.cs` (new) — parse + compose resolver.
- `NexusApp/Services/ComponentStringReference.cs` (new) — embedded `components.ini` →
  `key → officialName`.
- `NexusApp/Data/components.ini` (new embedded resource).
- `NexusApp/Services/GameLogBlueprintImporter.cs` — add step 3 resolution hook.
- `NexusApp/Services/GameLogSession.cs` — build the resolver from the derived global.ini path.
- `NexusApp/Models/AppSettings.cs` — add `GlobalIniPath`.
- `NexusApp/Views/LogMonitorWindow.cs` (+ Settings) — global.ini path picker + status line.
- `NexusApp.Tests/GlobalIniReaderTests.cs`, `ComponentStringReferenceTests.cs` (new) +
  additions to `GameLogBlueprintImporterTests.cs`.
