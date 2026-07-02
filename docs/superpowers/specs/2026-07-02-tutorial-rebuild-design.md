# Welcome Tour Rebuild - Design

Date: 2026-07-02
Branch: feature/help-tutorial-refresh
Status: approved by T3SoD

## Goal

Replace the legacy welcome tour content (a patched-up feature inventory dating back to the
auto-scan-only era) with a ground-up flow that covers the current app: the MOBIGLAS shell,
the module dock, Operations, the overlay HUB, Cargo Hauling, the Blueprint Network, and the
always-on Game.log passives. The tour teaches the map, not the manual: a user who finishes
(or bails halfway) should know where things live and what runs with zero setup.

## Mechanics (unchanged engine)

The existing coach-mark engine stays as-is: `TourController` drives an ordered `Step[]`
(title + caption), each step anchored to a live control with a `HighlightWindow` ring and a
`CoachMarkWindow` bubble (Back / Next / Skip); a null anchor centers the bubble. The final
step relabels Next to "Set up auto-scan" and hands off to the scan-region draw flow. Shown
once on first run (`MaybeShowFirstRunWizard`), replayable from Help. Mouse only, no keyboard
paths, per app policy.

## Design basis

Three flow designs were generated (first-session journey / zero-config proof-first /
spatial mental-map) and scored by a three-lens judge panel (new player, UX critic, veteran
relearning the redesign). The spatial design won 2-1; the final flow below is its skeleton
with the judges' unanimous grafts: zero-config's proof-first opener, journey's Refinery and
Operations coverage, and both closing mnemonics.

## The flow (14 steps, final copy)

| # | Title | Anchor | Caption |
|---|-------|--------|---------|
| 1 | Already working | centered | Nexus went live the moment you launched it - your game feeds it everything through one log file, no setup required. This quick tour shows you the map: a status strip that reports, a module dock that works, and an overlay that flies with you. |
| 2 | The status strip | GAME SESSION header pill | This pill lights up when Star Citizen writes to Game.log and Nexus reads along - read-only, nothing modified, EAC-safe. If it stays dark, point Settings at your Game.log path. |
| 3 | Blueprints count themselves | BLUEPRINTS header pill | Pick up a blueprint in the verse and this count ticks on its own - every find lands in your Blueprint Library with zero clicks. WHERE TO MINE there shows how to get the ones you are missing. |
| 4 | The module dock | whole dock | Eight modules, one click each. When you want to do something instead of glance at it, it lives on this dock - Settings included, down at the bottom. |
| 5 | Operations preflight | KPI row on the Operations page | Nexus lands here on launch. Read the row - last scan, refinery queue, cargo in transit, session blueprints, network coverage - and every panel below links straight into its module. |
| 6 | RS Decoder | RS Decoder dock tile | Your first stop after scanning a rock: click in, type the RS value, and get a best-match ore breakdown. Or skip the typing - auto-scan can read it off your screen. |
| 7 | Refinery | Refinery dock tile | Click + Add work order inside and Nexus counts down every job. The pill on this tile shows how many orders are cooking. |
| 8 | Hauling tracks itself | Cargo Hauling dock tile | Accept a hauling contract in game and it appears here on its own - the pill shows your active hauls. Click in for consolidated collect and deliver stops across all of them. |
| 9 | Share without a server | Network dock tile | Network trades blueprint libraries by file - export yours, import a friend's, see who owns what together. Fully offline; nothing leaves your machine unless you hand it over. |
| 10 | The third space | Overlay toggle button (header) | Click here to launch the overlay - a compact panel that floats over Star Citizen so you never leave the game to use Nexus. |
| 11 | Proof of life | Overlay HUB tab (SCAN STATUS lights) | The overlay opens on the HUB: green lights mean a feed is live right now - session, RS auto-scan, contracts. Blueprints collected, server, and shard read out below. |
| 12 | Auto-scan is opt-in | Auto-scan RS switch (overlay SCAN tab) | Switch this on and Nexus reads rock signatures straight off your screen through the magenta box you draw once. It pauses on its own whenever you and the game are both in the background. |
| 13 | Contracts get their own box | Set contract detection region link (overlay HAULING tab) | Contract scanning uses a separate region, set right here - yellow for contracts, magenta stays RS. The two never interfere. |
| 14 | You have the map | centered | Strip reports, dock works, overlay flies with you - and the loop is track, scan, refine, haul, share. One thing left: click Set up auto-scan to draw your magenta RS box now, or replay this tour anytime from Help. |

Steps 6-9 ring their dock tiles WITHOUT navigating to the page (keeps the main window
stable through the dock run, per the judges); step 5 navigates to the Operations page
first; steps 11-13 open the overlay and switch its tab via the tutorial helpers.

## Deliberate cuts

- Mining Codex, Shopping List, About, clock: discoverable from the dock / header; every
  extra step raises skip rates.
- Dedicated Settings stop: its recovery role lives in step 2's caption ("point Settings at
  your Game.log path") and step 4 shows where Settings sits.
- SHARD and SCAN header pills: duplicated by the HUB step.

## Implementation notes

- `TutorialTarget` enum is replaced wholesale: `None, SessionPill, BlueprintsPill, AppDock,
  OperationsKpis, RsDecoderTile, RefineryTile, HaulingTile, NetworkTile, OpenOverlay,
  OverlayHub, ScanToggle, ContractRegion`. Old members that no longer exist are removed
  (this enum is internal to the tour; no persistence).
- New anchors required:
  - GAME SESSION and BLUEPRINTS pills: give their container Borders x:Name in
    MainWindow.xaml (`SessionChip`, `BlueprintChip`).
  - Operations KPI row: expose a `KpiRowTarget` FrameworkElement on CommandPage and a way
    for MainWindow's resolver to reach the live CommandPage instance.
  - Dock tiles: NavScan (RS Decoder), NavWork (Refinery), NavHauling, NavNetwork,
    DockTiles (whole dock) already have names.
  - Overlay: `HubTarget` / `ShowHubTabForTutorial()` exist; add `ContractRegionTarget =>
    SetContractRegionBtn` and `ShowHaulingTabForTutorial() => SwitchTab("hauling")`, and
    extend `PrepareOverlayForTutorial` to select scan / hub / hauling.
- First-run gate, Help replay, Skip behavior, and the "Set up auto-scan" finale handoff
  are unchanged.
- Copy rules: no emojis, no em-dashes (" - " separators), mouse verbs only, no upstream
  contributor names.

## Testing

- `dotnet build` clean (0 warnings / 0 errors).
- Manual run-through of all 14 steps via Help > Replay Tutorial: every anchored step rings
  the right control, page/tab navigation settles before the ring lands, Back from step 6
  returns cleanly to the Operations page state, Skip works at any point, finale launches
  the region selector.
- Screenshot verification of representative steps (2, 5, 11, 13) during the run-through.
- First-run path: delete %APPDATA%\NexusApp\settings.json, launch, confirm the tour
  auto-shows once and never again.
