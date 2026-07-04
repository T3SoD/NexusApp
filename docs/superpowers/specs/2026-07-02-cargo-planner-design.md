# Cargo Planner Design Spec

**Date:** 2026-07-02
**Status:** Approved (Zach), ready for implementation plan.
**Branch:** `feature/cargo-planner`

## Goal

A standalone NexusApp dock module that helps a Star Citizen hauler pack the cargo they must move into their ship's cargo grids, shown in 3D. The user supplies cargo (manual entry or one-click import from active in-game hauls); the app auto-packs standard SCU containers into the selected ship's grids, spanning multiple trips when needed; the user can then drag individual boxes to adjust. This is the reserved "ship-fitment" feature, so the "Manifest Stack" settle animation lives here.

## Architecture (the load-bearing decision)

One integer voxel-occupancy model per grid, plus a list of `Placement` records, is the single source of truth. The auto-packer and the drag validator call the identical `Fits()` / `IsSupported()` / `Mark()` primitives, so a hand-dragged layout can never violate a rule the packer enforced. All placement math is integer cell arithmetic (1 cell = 1 SCU = a 1.25 m cube), so the pack is a pure function of (ship, manifest): byte-identical every run, logged with a layout hash.

## Verified data (embedded at build time, no runtime network)

### Ship cargo grids (FleetYards, cross-verified)
93 cargo ships, 284 grids, zero irregular. Every grid is a clean integer cell lattice: `dimensions_meters / 1.25 = integer cells` on each axis, and `cellX * cellY * cellZ == scuCapacity` exactly. Each grid also carries a `maxContainerSize` (one of 1, 2, 4, 8, 16, 24, 32 SCU). Source dataset already extracted to `scratchpad/cargo_ships.json`; it will be normalized (vertical axis identified) and embedded.

### Standard SCU container footprints (cross-verified two independent ways)
Confirmed both by scraping the SC wiki and by reconstructing from FleetYards `maxContainerSize.dimensions` across all 93 ships. One consistent footprint per size, volume equal to SCU exactly. Convention: `cellsX` = long horizontal axis, `cellsY` = other horizontal axis, `cellsZ` = vertical (gravity).

| SCU | cellsX | cellsY | cellsZ | Shape |
|-----|--------|--------|--------|-------|
| 1 | 1 | 1 | 1 | cube, orientation-free |
| 2 | 2 | 1 | 1 | flat domino, 1 tall, yaw-rotatable |
| 4 | 2 | 2 | 1 | flat slab, 1 tall, orientation-free (square floor) |
| 8 | 2 | 2 | 2 | cube, orientation-free |
| 16 | 4 | 2 | 2 | 2 tall, yaw-rotatable |
| 24 | 6 | 2 | 2 | 2 tall, yaw-rotatable, main fragmenter |
| 32 | 8 | 2 | 2 | 2 tall, yaw-rotatable, longest |

Rotation model [Likely]: containers snap upright and rotate only in 90-degree yaw (swap cellsX/cellsY); they never tip onto a side, so `cellsZ` (height) is fixed. Cubes (1, 8) and the square slab (4) are rotation-invariant. The packer's fit test is footprint-and-orientation based, never volume based.

## Packing algorithm

Support-constrained Bottom-Back-Left First-Fit-Decreasing (voxel BLF).

1. **Split** each manifest line's total SCU into physical boxes, greedy largest-first over {32, 24, 16, 8, 4, 2, 1}, capped at `effectiveCap = min(lineCap ?? 16, shipMaxCap)` so an unholdable box is never created. 1 SCU divides everything, so the remainder is always 0.
2. **Order** all boxes by a strict total order: volume desc, SCU desc, cellsX desc, cellsY desc, cellsZ desc, then stable input index. Order grids by capacity desc, then maxContainerSize desc, then grid id asc.
3. **Place** each box, first-fit across grids (skipping any grid where `box.scu > grid.maxContainerSize`), with a Bottom-Back-Left scan inside a grid: Y ascending (lowest first = gravity), then Z ascending, then X ascending; try native orientation then 90-degree yaw; accept the first anchor that is in-bounds, all cells free, AND fully supported (Y=0 floor, or every cell directly beneath occupied). Mark cells, record a `Placement`.
4. Boxes that fit nowhere go to a **deferred** list (deterministic order).
5. A cheap **gap-fill retry** re-attempts deferred 1/2/4 SCU boxes against the final occupancy.

Deterministic: integer math, strict total orders, fixed scan/orientation order, first-fit acceptance. Same input -> byte-identical layout, verified by a logged layout hash.

## Multi-trip

First-class, never an error. Real box-level replay: pack against fresh empty grids; boxes that place are trip 1; deferred boxes pack into a fresh ship-load for trip 2; repeat until empty. A trip selector switches which packed layout the 3D view renders. This planner is also the primitive the ship-fit search calls.

## Ship-fit search

Given the manifest, run the multi-trip planner against all 93 embedded ships; compute fits-in-one-trip, trips-needed, best-trip utilization. Present sorted fewest-trips then tightest utilization, so the smallest ship that does it in one trip floats up. It evaluates the real footprint pack, so a ship with SCU headroom but a box mix that will not physically fit shows "SCU ok, shape fails", not a false green. Runs on demand, milliseconds, fully offline.

## Cargo input and box-cap handling

The box mix depends on the contract's max-container cap, which lives in the contract text, not the SCU number and not the Game.log.

- **Manual entry:** a compact inline chamfered form (no modal): label (optional, cosmetic), SCU (required numeric stepper), cap (dropdown of {1,2,4,8,16,24,32, Unknown}, default Unknown resolving to 16 and marked "assumed").
- **Import from active hauls:** reads `App.Hauls` (existing `HaulTracker`), reusing the existing Game.log parser. Each active leg contributes a line with commodity (`HaulLeg.Commodity`) and SCU (`HaulLeg.TargetScu`) known, cap unknown. The cap gap is filled by extending the existing pure `ContractParser` with one regex for "delivered in N SCU containers or smaller", attaching it as `ContractObjective.ContainerCap` (one added field). Does NOT touch the locked `OcrService.cs` preprocessing.
- **Provenance** (`Ocr | User | Default`) is first-class on every line. Default-16 lines show an amber "assumed" marker with a one-click override. The cap only affects the physical split, never the total SCU, so an unknown cap never lies about how much cargo there is.
- Merge: all active hauls import into one load; same commodity + same cap merges SCU; same commodity + different caps stay separate rows; re-import matches on MissionId (else commodity+cap+source) and replaces rather than duplicates. Reads only commodity/SCU/cap, never handle/username.

## 3D viewer (built-in WPF Viewport3D only, no HelixToolkit, no native libs)

One `PerspectiveCamera` + one `Viewport3D`. Every container is its own `GeometryModel3D` over a single Frozen unit-cube `MeshGeometry3D`, via a `MatrixTransform3D` (scale = cell dims x 1.25 m, translate = cell anchor), colored by one of 7 Frozen size-keyed materials. Scene split into STATIC (grid floors + faint walls) and MOVING (containers) `ModelVisual3D` roots. Grids drawn as translucent `ImageBrush` floor quads tiled with the 1.25 m cell texture plus faint bounding walls and a baked grid-id decal; never line-soup, never live 3D text (documented-slow). Perf hygiene: Freeze mesh + materials, `ClipToBounds=false`, `EdgeMode=Aliased`, 1-2 lights, small per-box inset against z-fighting.

- **Camera:** spherical turntable state (radius, azimuth, elevation clamped, target = grid center) recomputed into Position/LookDirection/UpDirection each change. Left-drag orbit, right/middle-drag pan, wheel zoom (radius, clamped, never FOV). `CaptureMouse` on button-down. Mouse-only, no hotkeys.
- **Pick:** `VisualTreeHelper.HitTest` nearest-first, cast to `RayMeshGeometry3DHitTestResult`, Stop on first mesh, map `ModelHit` through a `Dictionary<GeometryModel3D, Placement>`.
- **Drag:** hit-test a coplanar invisible drag-plane at the selected layer's height (base .NET 8 WPF has no `Unproject`), take the `PointHit` delta, snap to lattice by floor-division, validate against occupancy. Disable container hit-testing during the drag so cost is constant regardless of box count. Ice-cyan valid / amber invalid material swap; commit only on a valid drop.
- **Vertical placement:** an on-screen mouse layer selector chooses which horizontal plane a drag snaps to (the no-hotkey substitute for a modifier). Flagged as the weakest UX moment; prototype early with a Framer mockup via ui-ux-pro-max.
- **Multi-grid ships** render their grids in a synthetic side-by-side layout (FleetYards gives grid dimensions but not true inter-grid offsets inside the hull). Stated as an in-app fidelity caveat; single-grid ships are faithful.
- **Box-count soft cap** (~800 draggable): past it, settled boxes render as static per-color merged meshes (view-only); the actively dragged box stays individual.

## Screen layout

One dock tile, one full-module screen, three zones in MOBIGLAS amber-on-void (ice-cyan reserved for live/valid readouts):
- **Left rail (~300px):** ship selector (searchable, last-used default) on top; then the manifest list (per row: label, SCU, cap with provenance dot, source chip, trip chip, inline SCU stepper, delete); an inline "Add line" form and an "Import from hauls" button; a bottom aggregate readout (manifest SCU / ship SCU, fill meter, remainder + trips, amber "remaining manifest" sub-panel when over capacity).
- **Center (~55-60%):** the single `Viewport3D` showing the selected trip packed into the selected ship, with a thin bottom mouse control cluster (reset-view, per-grid focus, trip segmented selector, layer selector).
- **Right rail (~280px):** selected-box card (SCU, label, grid id, anchor cell, orientation, validity chip with reason); per-grid fill panel (selecting a grid focuses the camera); ship-fit results; size/commodity legend.

All feedback is inline color + rail text. No popups, no toasts anywhere.

## File breakdown

- `Models/Cargo/CargoShip.cs` - `ShipCargoDef` + `GridDef` (embedded geometry, vertical axis normalized).
- `Models/Cargo/CargoBox.cs` - `BoxType` 7-row table (cellsX*Y*Z == scu, isCube, orientations) + `Box` instance.
- `Models/Cargo/ManifestItem.cs` - runtime line (label, scu, cap, capSource, source, gameLogMissionId, colorId).
- `Models/Cargo/Placement.cs` - `Placement` record + `Trip` container + deferred list.
- `Data/cargo_ships.json` - embedded FleetYards asset (EmbeddedResource, no network).
- `Services/Cargo/CargoShipCatalog.cs` - loads + validates the json (asserts scuCapacity == cw*ch*cd), searchable ship list.
- `Services/Cargo/GridOccupancy.cs` - `bool[x,y,z]` per grid + shared `Fits()`/`IsSupported()`/`Mark()`.
- `Services/Cargo/CargoPacker.cs` - split, comparators, tryPlace, autoPack, gap-fill (pure, headless, unit-tested).
- `Services/Cargo/MultiTripPlanner.cs` - box-level replay across trips.
- `Services/Cargo/ShipFitSearch.cs` - ranks all 93 ships by real footprint pack.
- `Services/Cargo/CargoImportService.cs` - reads `App.Hauls` into `ManifestItem`s, joins OCR caps by MissionId/contractor.
- `Services/ContractParser.cs` (EXTEND) - add the cap regex.
- `Models/ContractDetails.cs` (EXTEND) - add `int? ContainerCap` to `ContractObjective`.
- `ViewModels/CargoPlannerViewModel.cs` - manifest, pack/trip state, ship + fit selection, drag orchestration.
- `Views/CargoPlannerPage.cs` - three-zone shell on shared `Hud`/`ChamferPanel` primitives (mirrors HaulingPage/NetworkPage).
- `Views/CargoManifestRail.cs` - left rail.
- `Views/CargoInspectorRail.cs` - right rail.
- `Views/CargoViewport.cs` - the Viewport3D scene builder.
- `Views/CargoTurntableCamera.cs` - spherical camera + mouse orbit/pan/zoom.
- `Views/CargoInteractor.cs` - pick + drag-plane hit-testing, snap/validate/commit, layer selector.
- `Views/ManifestStackAnimator.cs` - the reserved short settle reveal.
- Dock nav wiring (extend `DockIconSpecs.cs`, MainWindow/MainViewModel) - register the tile.
- `NexusApp.Tests/Cargo/` - `CargoPackerTests`, `MultiTripPlannerTests`, `ShipFitSearchTests`, extend `ContractParserTests`.

## Logging

Single `[CARGO]` tag via `Logger.Info("[CARGO] ...")` + `InteractionLog.Nav("Cargo Planner")` on open, so it surfaces in the App Log Monitor and diagnostic snapshot (definition-of-done). Events: nav, import (line count + caps from OCR vs defaulted), ship-select, pack (placedCount, deferredScu, per-grid utilization, trips, layoutHash), fit-search, trip-switch, pick, snap, reject (reason), commit, re-split. Only commodity/SCU/cap/cell data, never handle/username. No em-dashes, no emojis.

## v1 scope

Standalone dock tile; deterministic auto-packer with gap-fill; multi-trip planning; searchable ship selection with last-used memory; ship-fit search; both cargo inputs with cap provenance; the Viewport3D scene with mouse turntable camera, pick, and single-plane snap-drag with full validation; on-screen layer selector; inline over-capacity handling with re-split-smaller; short skippable Manifest Stack settle reveal; `[CARGO]` logging; box-count soft cap with static fallback.

## Deferred

Saving/naming/exporting/sharing layouts; commodity price/mass/density; color-by-commodity as the base channel (v1 is color-by-size); on-demand re-split out of a coalesced mesh; relaxed free-float staging mode; cross-trip manual box reassignment; fleet/hangar management and ship 3D previews; tight fuzzy OCR-to-Game.log correlation; per-grid management UI; cinematic sequencing beyond the one settle reveal.

## Constraints

C# / .NET 8 / WPF, Windows only, built-in `System.Windows.Media.Media3D` (no HelixToolkit/native). Fully offline at runtime (all data embedded at build time). Mouse-driven only, no keyboard shortcuts. MOBIGLAS visual language. No popup/toast notifications. `[CARGO]` logging (definition-of-done). Public repo, zero PII in code/logs. No em-dashes, no emojis. `OcrService.cs` preprocessing is locked and untouched (the cap regex goes in the pure `ContractParser` only).

## Open risks

1. **Multi-grid 3D is synthetic side-by-side** (no true inter-grid offsets in the data); a planning aid, not a literal load map for multi-grid ships. State in-app.
2. **Vertical-axis normalization** of the 284 grids must be verified against known ships (Hull C, Freelancer MAX) at build time, or a mis-mapped vertical axis produces plausible-looking but wrong packs.
3. **Vertical layer selector** (no-hotkey) is the weakest UX moment; prototype early on a multi-layer Hull C load.
4. **Cap-OCR extension** is the only genuinely new capture work; imported hauls with no OCR cap default to 16 (safe, marked approximate). OCR-to-Game.log correlation is loose in v1.
5. **Drag-time container-hit-test-disable** is non-negotiable for perf; if regressed, drags stutter as box count grows.
6. **Rotation model** assumes upright yaw-only; if a future patch allows free tumbling, revisit.
