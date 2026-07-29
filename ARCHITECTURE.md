# Architecture

This document is a high-level map of how NexusApp is built. For the security
boundary (network access, file access, and game-process access), see
[SECURITY.md](SECURITY.md).

## Overview

NexusApp is a single-process **WPF desktop app** that targets **.NET 8**
(`net8.0-windows`). The build publishes NexusApp self-contained for `win-x64`.
NexusApp uses the **MVVM** pattern with `CommunityToolkit.Mvvm`. A thin services
layer sits under the UI.

All data is local. The build bundles the reference data. The user data is on
disk in the per-user app-data folder. Two subsystems are opt-in and touch the
network: the update check and the live market data fetch. Nothing else in the
app makes a network call.

```
+-----------------------------------------------------------+
|  Views (WPF)                                              |
|  MainWindow  +  OverlayWindow  +  dialogs / flyouts       |
+----------------------------+------------------------------+
                             |  data binding / commands
+----------------------------v------------------------------+
|  MainViewModel  (single shared instance)                  |
+----------------------------+------------------------------+
                             |  calls
+----------------------------v------------------------------+
|  Services                                                 |
|  Data | Ocr | Scanner | Settings | Theme |                |
|  GameLogFeed -> Session / Hauling / Shard | Importer |    |
|  Hauling(HaulTracker/Parser/Contract/Shard) |             |
|  Network(File/Store/Scope) |                              |
|  Update(Service/Verifier/Manifest/Notice) |               |
|  Market(DataService/Snapshot/NameMap/Queries/Notice) |    |
|  Guides(Catalog) | ExecHangarCycle |                      |
|  Overlay(Tabs/GhostGeometry/GhostFootprints) |            |
|  ScmdbImport(Parser/Plan) |                               |
|  UiScaleService |                                         |
|  PortableUpdater / SwapJournal (portable self-update) |   |
|  Diagnostics(CrashGuard/Breadcrumbs/Sanitizer/Notice) |   |
|  Logger / InteractionLog / ForegroundMonitor              |
+----------------------------+------------------------------+
                             |
+----------------------------v------------------------------+
|  Storage:  embedded seed_data.json  +  SQLite  +  JSON    |
|            settings  +  local network.db (Blueprint Net)  |
+-----------------------------------------------------------+
```

## Layers

### Views (`Views/`, `MainWindow.xaml`, `OverlayWindow.xaml`)
The Views are the WPF windows and dialogs. The two main surfaces are
**MainWindow** and **OverlayWindow**. **MainWindow** is the full NexusApp.
**OverlayWindow** is a compact panel. **OverlayWindow** stays always-on-top and
floats over the game.

**MainWindow** shows one dock page at a time. `GuidesPage` is the Mission
Guides dock page: a category-grouped card grid over the shared `GuideCatalog`.
Clicking a card hands the page over to `GuideViewer`, a zoom-and-pan image
view shared with the overlay.

**OverlayWindow** shows the same tabs in a compact strip, `OverlayTabStrip`,
where the active tab expands into an amber pill and the rest show only their
icon. Ghost mode replaces this strip with `OverlayGhostRail`, a narrow
vertical icon rail with a per-tab flyout, for a smaller footprint over the
game.

Supporting windows include:
- the region selector for auto-scan
- the toast and scan-indicator popups
- the app-log monitor
- the settings, about, and help dialogs
- the SCMDB import preview and result dialogs (`ScmdbImportResultDialog`)

### ViewModel (`ViewModels/MainViewModel.cs`)
Both the main window and the overlay share a single **MainViewModel** instance.
This design keeps the two surfaces in sync. A button on the overlay and the
matching control on the main window bind to the same observable state and
commands. So a change on one surface also shows on the other surface. No manual
copy is necessary.

Scan results and refinery work orders flow through the same view model. So they
stay consistent everywhere.

### Services (`Services/`)
The view model controls these services. Each service holds little or no state.

- **DataService** - loads the reference data (resources and blueprints). It
  saves the user data with SQLite and JSON.
- **OcrService** - captures a screen region. It runs Windows OCR, the native OCR
  engine. It prepares the image (invert, contrast, and upscale). It reads the RS
  value from the recognized text.
- **ScannerService** - runs the opt-in auto-scan loop. It reports the readings.
- **SettingsService** - loads and saves `AppSettings`. It also moves app data
  from the old folder name.
- **ThemeService** - provides the single theme resources of NexusApp (merged
  palette, icon URIs, and logo URIs). v6.0.0 removed the luxury/classic theme
  picker.
- **GameLogFeed / GameLogSession / GameLogWatcher / GameLogBlueprintImporter /
  RsiHandleParser** - the read-only `Game.log` subsystem (see below).
- **ComponentStringReference / GlobalIniReader** - translate mod-renamed
  blueprint names back to their official names. They join the user's read-only
  `global.ini` to the bundled `components.ini`. The `Game.log` import path uses
  them.
- **Hauling (HaulTracker / HaulLogParser / ContractOcrService / ContractScanner /
  ShardTracker / ShardLogParser / ContractCapCatalog)** - the cargo-hauling
  subsystem (see below). It reads `Game.log` read-only.
- **Network (NetworkFileService / NetworkStore / NetworkScope)** - the
  file-exchange subsystem for the offline Blueprint Network (see below).
- **Update (UpdateService / UpdateVerifier / UpdateManifest / UpdateNotice)** -
  the opt-in update subsystem (see Data and storage). Only `UpdateService` (with
  its HTTP transport) touches the network. The other three do no network work:
  signature and hash verification, manifest parsing, and the notice text.
- **Market (MarketDataService / MarketSnapshot / MarketSnapshotFile /
  MarketNameMap / MarketQueries / MarketNotice)** - the opt-in live market data
  subsystem. `MarketDataService` runs the hourly fetch cycle against the UEX
  community API (see SECURITY.md); it and `UpdateService` are the only two
  places in the app that touch the network. `MarketSnapshot` is the in-memory
  price cache; `MarketSnapshotFile` persists it to
  `%AppData%\NexusApp\cache\uex_snapshot.json` and reloads it at startup, so the
  last fetched prices still show when Nexus is offline. `MarketNameMap` links
  the seed's raw resource names to UEX's commodity names and their refined
  counterparts. `MarketQueries` is the pure read layer. The RS Signal Decoder,
  the Mining Codex, the Refinery Tracker, and the overlay's own scan cards
  (`OverlayWindow.FillMarketSell` / `RefreshMarketSellLines`) all call it for a
  priced hit. `MarketNotice` holds the feature's user-facing copy (the consent
  strip, the Settings section, the source note), mirroring `UpdateNotice`.
  Consent is a tri-state setting, `MarketDataEnabled` (null = unanswered,
  true/false = the user's standing choice), the same pattern as the
  update-check toggle. The toggle sits in the Settings UPDATES tab, in its own
  Market Data section next to the update check, not under Diagnostics.
- **GuideCatalog** - the single source of truth for the Mission Guides feature.
  Each entry is one curated guide image: an id, a title, a category, an
  embedded PNG resource, and its native pixel size. `GuidesPage` and the
  overlay's GUIDES tab both read this one list, so a guide added once shows on
  both surfaces.
- **ExecHangarCycle** - pure, deterministic math for the PYAM contested-zone
  Executive Hangar cycle (see Key flows). It computes the open/closed phase,
  how many dots are lit, and the next few open times. The instant it starts
  from is one compiled-in anchor timestamp, or a user re-anchor override
  stored in settings. The caller supplies the clock, so every phase boundary
  is unit-testable.
- **OverlayTabs** - tab identity and the restore rule for the overlay's tab
  strip. It holds the id list and the display label per tab. A guard falls
  back to the default tab on an unknown or stale saved id. This is kept out
  of the WPF view so the guard is testable headlessly, the same split
  `SettingsTabs` uses for the Settings page.
- **GhostGeometry / GhostFootprints** - pure placement and sizing math for the
  overlay's ghost mode (see Key flows). `GhostGeometry` decides which way a
  collapsed rail expands. It clamps every rect to the monitor. All of this is
  in physical pixels. A Per-Monitor-DPI V2 lesson requires it: DIP
  positioning can land a window on the wrong monitor at a DPI boundary.
  `GhostFootprints` turns the rail scale and the panel scale into window
  sizes for the collapsed rail, the expanded panel, and the gear flyout.
- **UiScaleService** - the app-wide UI scale. It holds two persisted scale
  factors: one for the main window and dialogs, one for the overlay. Each
  factor applies as a `LayoutTransform` on a window's root element. A third,
  independent factor scales the ghost rail alone, from 0.75 to 1.5. This
  floor sits below the 1.0 floor of the other two factors, because the
  rail exists to keep the footprint small.
- **ScmdbExportParser / ScmdbImportPlan** - the SCMDB import subsystem (see Key
  flows). `ScmdbExportParser` never throws: a bad or oversized file comes back
  as a clear error instead of a crash. `ScmdbImportPlan` is the pure,
  add-only bucketing of an export's completed blueprints into names to
  import, names already owned, and unrecognized names.
- **PortableUpdater / SwapJournal** - the portable self-update (see Key flows).
  PortableUpdater verifies the download again on one open file handle, unpacks
  it, and verifies each file. It then replaces the app's files with journaled
  renames. It rolls the renames back if a step fails while the app is open.
  SwapJournal is the write-ahead record of each rename. NexusApp reads
  SwapJournal at the next start. It removes the `.old` files after a complete
  swap. It puts the previous version back after an incomplete one.
- **Logger / InteractionLog / ForegroundMonitor / DiagnosticSnapshot** - the
  diagnostics. These are a self-rotating event log, UI-interaction breadcrumbs,
  tracking of the foreground window and process, and the copy/save diagnostic
  bundle. This tracking records the process name only, never window titles.
- **CrashGuard / SystemEventBreadcrumbs / TextSanitizer / RelaunchNotice** - the
  newer diagnostics. CrashGuard restarts NexusApp one time after a display or
  render error. SystemEventBreadcrumbs logs display, power, and session changes.
  TextSanitizer cleans untrusted text from imported files before it reaches the
  log. RelaunchNotice gives the auto-restart notice text to the dashboard and
  the Settings page.

### Models (`Models/`)
The Models are plain data types: `Blueprint`, `Resource`, `WorkOrder`,
`ShoppingItem`, `AppSettings`, and the `NetworkFile` / `NetworkModels` exchange
types.

## Data and storage

- **Reference data** ships as `Data/seed_data.json`. The build embeds it into
  the assembly as a resource. It is the single source of mining and blueprint
  data. It ships inside each release. There is no data-only over-the-air path.
  A second embedded reference, `Data/components.ini`, maps internal component
  keys to their official names. The build refreshes it for each game patch.
- **User data** (settings, work orders, and the owned-blueprint library) is on
  disk in the per-user app-data folder. NexusApp stores it with SQLite and JSON.
- **Settings recovery.** NexusApp writes `settings.json` with a
  write-temp-then-replace step (`File.Replace`). This step is atomic on the
  same volume and keeps the previous good copy as `settings.json.bak`, so a
  crash mid-write never leaves a truncated file as the only copy. If
  `settings.json` fails to load, NexusApp moves the unreadable file aside as a
  timestamped `.corrupt-` file for diagnosis, then tries `settings.json.bak`,
  and falls back to fresh defaults only when neither file reads back cleanly.
  NexusApp re-persists the recovered file right away, not only on the next
  user-triggered save. The same rule applies after a schema migration, so a
  recovered or migrated file is not lost if the app closes without a clean
  shutdown.
- **Blueprint Network** uses a separate local `network.db`. It exchanges
  `.nexuslib` files that the user moves by hand. Nothing syncs automatically.
- **Versioning** comes from one source: the `<Version>` in
  `NexusApp/NexusApp.csproj`. The in-app badges and the installer read it from
  the built executable. For releases, CI overrides it from the git tag.
- **Updates** are opt-in. The app can check for new releases when the user turns
  the check on. `scripts/sign_release.ps1` hashes the published release assets
  locally, writes `update_manifest.json` and a detached `.sig` (ECDSA P-256 over
  SHA-256), signs with a passphrase-protected key kept off GitHub, and uploads
  both to the release. `scripts/generate_update_keys.ps1` creates the keypair,
  and `scripts/protect_update_key.ps1` is the one-time step that locks the
  private key behind that passphrase. In the app, `UpdateVerifier` holds the
  pinned public key, the hash checks, and the strictly-greater version rule. It
  gates `UpdateService`. Downloads land in `%AppData%\NexusApp\updates`. NexusApp
  checks their hash before the installer ever runs. The installer flavor runs the
  verified installer file. The portable flavor can install the update itself,
  without a helper program and without a script. See Portable self-update below.
- **Live market prices** are opt-in, gated by the tri-state `MarketDataEnabled`
  setting (null = the one-time consent strip has not been answered, true/false =
  the user's standing choice). When enabled, `MarketDataService` fetches sell
  prices from the UEX community API about once an hour while NexusApp is open,
  and this fetch is the only other network code in the app besides the update
  check. The fetched snapshot is cached at
  `%AppData%\NexusApp\cache\uex_snapshot.json` (`MarketSnapshotFile`), so the
  last fetched prices load back on the next start and NexusApp works from them
  fully offline between fetches. The bundled mining seed data itself is not
  fetched from anywhere; only its prices are enriched from UEX when the user
  opts in.

## Key flows

### Auto-scan (RS Signal Decoder)
1. The user draws a scan region over the RS value (RegionSelectorWindow).
2. `ScannerService` asks `OcrService` to capture that region at regular
   intervals.
3. `OcrService` prepares the capture. It inverts the colors, so light-on-dark
   game text becomes dark-on-light. It boosts the contrast and upscales 6x. Then
   it runs Windows OCR.
4. NexusApp parses the recognized text into an RS integer. The view model
   decodes that integer into the matching resource and node count.

### Session Tracking (always on)
Session Tracking is always on. NexusApp has no user toggle for it.
`GameLogWatcher` reads the plain-text `Game.log` of the game as the game adds new
lines. It opens the file read-only. `GameLogBlueprintImporter` finds the
"Received Blueprint" notifications. It marks those blueprints as owned.

There is exactly ONE `Game.log` watcher in the app. `GameLogFeed` owns it and
sends each line to all of its consumers: the blueprint session, the haul tracker,
and the shard tracker. Each consumer says whether it also wants the history that
a start from the top of the file replays. The haul and shard trackers want it,
because they rebuild their state from the whole log. The blueprint session does
not, because it must collect only what the game writes next. A consumer can
detach (the Stop button of the advanced monitor) without stopping the tail for
the others. The feed also probes once whether the game process runs. It never
opens a handle to that process.

A localization mod can rename blueprint names. NexusApp translates these names
back to their official names. It joins the user's read-only `global.ini` to the
bundled `components.ini`. NexusApp finds `global.ini` next to `Game.log`, or the
user sets the path.

`GameLogSession` is an app-lifetime hub. It ties the watcher, the importer, and
the per-session tally together.

### Cargo hauling (always on)
`HaulTracker` is a consumer of the shared `GameLogFeed`. It builds a haul for
each cargo mission. It runs separately from the blueprint session: stopping that
session does not stop haul tracking.

`HaulLogParser` is a pure static parser. It turns the haul log lines into
records. It does no file work. It reads no player identity.

`ContractOcrService` and `ContractScanner` run a second scan region. The user
draws this region over the in-game Contracts panel. This scan is optional. It
adds the reward, the contractor, and the cargo details to the matching haul.

`ShardTracker` and `ShardLogParser` read the shard join lines. They keep the
recent server and shard list. `ContractCapCatalog` is an embedded table. It maps
each contract to its container size in SCU. The `Haul` model holds the haul state.

### Mission Guides
`GuideCatalog` is the single list of curated guide images. Each entry names a
PNG the build embeds as a resource, and carries its native pixel size for the
zoom and fit math. `GuidesPage` shows the catalog as a category-grouped card
grid on the main window. The overlay's GUIDES tab shows the same catalog as a
compact list. Both surfaces open the same shared `GuideViewer` to show one
guide at full size.

`GuideViewer` is mouse only. The wheel zooms on the cursor position. A left
drag pans the image. A double click resets the view to fit. `GuideViewer`
never decodes a bitmap above its native size. It decodes at about twice the
viewport width until the user zooms past that point, then it decodes once
more at full size. The overlay caps this decode at a lower size, because it
never shows a guide this large on screen.

### Executive Hangar timer
`ExecHangarCycle` computes the state of the PYAM contested-zone Executive
Hangar rotation. The cycle has a fixed open phase, then a fixed closed phase,
repeating from one compiled-in anchor timestamp. The math is pure. It takes
the current time as a parameter, so every phase boundary has a test.

A user can re-anchor the cycle from the current moment, for example after a
game patch shifts the schedule. NexusApp stores this override in settings and
uses it in place of the built-in anchor until the user resets it.

`ExecHangarStatusLine` is the one control that shows this state. NexusApp uses
the same control on the Guides page, in the Contested Zones section header,
and in the overlay's GUIDES tab. This shared control keeps both surfaces the
same. The control owns its own timer. The host page starts the timer on entry
and stops it on exit.

### Overlay tab strip and ghost mode
`OverlayTabs` holds the tab id list and the display label for each tab. This
is the single source that both `OverlayTabStrip` and `OverlayGhostRail` read,
so the two surfaces always agree on which tabs exist.

`OverlayTabStrip` is the overlay's normal-mode tab row. The active tab expands
into a chamfered, amber pill with an icon and a label. Each other tab shows
only its icon, with a hover chip for the label. A small badge on a tab's icon
shows a count when one applies.

Ghost mode replaces this row with `OverlayGhostRail`, a narrow vertical icon
rail with the same tabs. A settings gear and a close glyph sit at the bottom
of the rail, for a smaller footprint over the game. `GhostGeometry` decides
which side of the rail a flyout or the expanded panel opens toward. It grows
toward the monitor's center, and it clamps every rect to stay on the
monitor. `GhostFootprints` turns the independent rail scale and the
overlay's own UI scale into the window sizes: the collapsed rail, the
expanded panel, and the gear flyout. This math works in physical pixels
throughout, because DIP positioning can land a window on the wrong monitor
at a Per-Monitor-DPI V2 boundary.

A quick-settings flyout holds the ghost-mode toggle, the click-through-in-FPS
toggle, the opacity slider, and the rail-size slider. NexusApp builds it once,
lazily, inside the overlay window. The overlay header's own gear button
(normal mode) and the ghost rail's gear button open this same flyout.

### SCMDB import
NexusApp can import a one-time blueprint-tracking export from the SCMDB
community website. `ScmdbExportParser` reads the export's JSON text and never
throws. A bad or oversized file (over 5 MB) comes back as a clear error
message in place of a crash.

`ScmdbImportPlan` sorts the export's completed blueprints into three buckets:
names to mark owned, names already owned, and names NexusApp does not
recognize. Import is add-only. No step in this feature ever un-marks a
blueprint.

Name resolution reuses the same official-name and localization pipeline the
`Game.log` importer uses (`GameLogSession.ResolveName`). So a modded or
renamed blueprint name resolves the same way through either import path.

`ScmdbImportFlow` drives the whole run: pick a file, parse it, build the plan,
then show `ScmdbImportResultDialog` as a preview and confirm gate. NexusApp
marks nothing owned until the user confirms. Cancelling at any point,
including closing the preview dialog, applies nothing and logs nothing.

### Blueprint Network (offline sharing)
A user exports their owned-blueprint library to a `.nexuslib` file. The user
shares the file out-of-band, for example on Discord or a drive. When the user
imports files from other people, NexusApp builds a roster. `NetworkScope` creates
coverage views (who owns what, gaps, and single-owner risk). `NetworkScope` can
filter all of NexusApp to a single member. No server is involved.

### Portable self-update
The portable flavor of NexusApp can install an update itself. NexusApp does the
whole update while it is open. Then it restarts as the new version. No helper
program runs, and no script runs. The signed manifest format does not change.

1. NexusApp verifies the downloaded file against the signed manifest.
2. NexusApp opens that file one time and verifies it again on the open handle.
   So the bytes that NexusApp checks are the bytes that NexusApp unpacks.
3. NexusApp unpacks the file and keeps the hash of each unpacked file.
4. NexusApp copies the unpacked files into its own folder as a staged set.
5. NexusApp then does one file at a time. It verifies the staged copy again. It
   renames the current file to a `.old` name. It renames the staged file into
   place. `NexusApp.exe` is always the last file.
6. NexusApp restarts. The old process starts the new process as its last action.
7. The next start removes the `.old` files and the download.

A journal records each rename before that rename happens. NexusApp writes the
journal to the per-user app-data folder, not to the install folder. If the
update stops part way, the next start reads the journal and puts the previous
version back from the `.old` files. NexusApp keeps the verified download until
the new version starts one time. NexusApp treats the journal as untrusted input
when it reads the journal back.

NexusApp tries the self-update only when the conditions are safe. NexusApp does
not try it in a protected folder, on a network drive, or with a second NexusApp
window open. NexusApp then offers a manual update. When the user chooses it,
NexusApp verifies and unpacks the update and opens the new folder and the
current folder. The user then does one copy.

## Build, test, and CI

- **Build:** Run `dotnet build NexusApp/NexusApp.csproj -c Release`.
- **Tests:** xUnit-style tests in `NexusApp.Tests/` cover the non-UI logic. This
  logic is the log parsers, the trackers, blueprint ownership, import and export,
  RSI handle parsing, the text sanitizers, the restart notices, and diagnostics.
- **CI:** GitHub Actions build the app and run the full unit test suite on every
  push and PR to `main` (`build.yml`). On a version tag, GitHub Actions run the
  same test suite, then publish the installer and the portable zip (`release.yml`).
  `release.yml` also posts the changelog to Discord. After the tag build publishes,
  the maintainer runs `scripts/sign_release.ps1 -Tag vX.Y.Z` on a local machine,
  typing the key passphrase at the prompt, to add the signed update manifest to
  the release. GitHub's default code-scanning setup runs CodeQL static analysis,
  with no workflow file. Dependabot keeps the NuGet packages and the Actions
  current.
