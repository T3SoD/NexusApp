# Architecture

A high-level map of how Nexus is built. For the security boundary (network,
file, and game-process access), see [SECURITY.md](SECURITY.md).

## Overview

Nexus is a single-process **WPF desktop app** targeting **.NET 8**
(`net8.0-windows`), published self-contained for `win-x64`. It follows the
**MVVM** pattern using `CommunityToolkit.Mvvm`, with a thin services layer
underneath the UI. All data is local: reference data is bundled at build time,
and user data lives on disk under the per-user app-data folder. There is no
network code.

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
|  GameLog(Session/Watcher/Importer) | Network(File/Store/  |
|  Scope) | Logger / InteractionLog / ForegroundMonitor     |
+----------------------------+------------------------------+
                             |
+----------------------------v------------------------------+
|  Storage:  embedded seed_data.json  +  SQLite  +  JSON    |
|            settings  +  local network.db (Blueprint Net)  |
+-----------------------------------------------------------+
```

## Layers

### Views (`Views/`, `MainWindow.xaml`, `OverlayWindow.xaml`)
WPF windows and dialogs. The two primary surfaces are **MainWindow** (the full
app) and **OverlayWindow** (a compact, always-on-top panel that floats over the
game). Supporting windows include the region selector for auto-scan, the toast
and scan-indicator popups, the app-log monitor, and the settings / about / help
dialogs.

### ViewModel (`ViewModels/MainViewModel.cs`)
A **single `MainViewModel` instance is shared by both the main window and the
overlay.** This is the key to keeping the two surfaces in sync: a button on the
overlay and the matching control on the main window are bound to the same
observable state and commands, so a change made in one is reflected in the other
with no manual copying. Scan results and refinery work orders flow through the
same view model and therefore stay consistent everywhere.

### Services (`Services/`)
Stateless-ish helpers that the view model orchestrates:

- **DataService** — loads reference data (resources, blueprints) and persists
  user data via SQLite and JSON.
- **OcrService** — captures a screen region and runs the native Windows OCR
  engine. Handles the image preprocessing (invert, contrast, upscale) and parses
  the RS value out of the recognized text.
- **ScannerService** — drives the opt-in auto-scan loop and reports readings.
- **SettingsService** — loads/saves `AppSettings`, including app-data migration
  from the legacy folder name.
- **ThemeService** — switches between the luxury and classic themes.
- **GameLogSession / GameLogWatcher / GameLogBlueprintImporter /
  RsiHandleParser** — the read-only `Game.log` subsystem (see below).
- **ComponentStringReference / GlobalIniReader** — translate mod-renamed
  blueprint names back to their official names by joining the user's read-only
  `global.ini` to the bundled `components.ini` reference; used by the `Game.log`
  import path.
- **Network (NetworkFileService / NetworkStore / NetworkScope)** — the offline
  Blueprint Network file-exchange subsystem (see below).
- **Logger / InteractionLog / ForegroundMonitor / DiagnosticSnapshot** —
  diagnostics: a self-rotating event log, UI-interaction breadcrumbs, foreground
  window/process tracking (process name only, never window titles), and the
  copy/save diagnostic bundle.

### Models (`Models/`)
Plain data types: `Blueprint`, `Resource`, `WorkOrder`, `ShoppingItem`,
`AppSettings`, and the `NetworkFile` / `NetworkModels` exchange types.

## Data and storage

- **Reference data** ships as `Data/seed_data.json`, embedded into the assembly
  as a resource at build time. It is the single source of mining and blueprint
  data; there is no over-the-air update path. A second embedded reference,
  `Data/components.ini`, maps internal component keys to their official names and
  is refreshed per game patch at build time.
- **User data** (settings, work orders, owned-blueprint library) is stored under
  the per-user app-data folder via SQLite and JSON.
- **Blueprint Network** uses a separate local `network.db` and exchanges
  `.nexuslib` files that the user moves manually. Nothing syncs automatically.
- **Versioning** is single-sourced from the `<Version>` in
  `NexusApp/NexusApp.csproj`; the in-app badges and installer read it from the
  built executable, and CI overrides it from the git tag for releases.

## Key flows

### Auto-scan (RS Signal Decoder)
1. The user draws a scan region over the RS value (RegionSelectorWindow).
2. `ScannerService` periodically asks `OcrService` to capture that region.
3. `OcrService` preprocesses the capture (invert colors so light-on-dark game
   text becomes dark-on-light, boost contrast, upscale 6x) and runs Windows OCR.
4. The recognized text is parsed into an RS integer, which the view model
   decodes into the matching resource and node count.

### Session Tracking (Beta, opt-in)
`GameLogWatcher` tails the game's plain-text `Game.log` read-only;
`GameLogBlueprintImporter` recognizes "Received Blueprint" notifications and
marks those blueprints as owned. Blueprint names that a localization mod renamed
are translated back to their official names using the user's read-only
`global.ini` (auto-detected next to `Game.log`, or set explicitly), joined to the
bundled `components.ini`. `GameLogSession` is an app-lifetime hub that ties the
watcher, importer, and per-session tally together.

### Blueprint Network (offline sharing)
A user exports their owned-blueprint library to a `.nexuslib` file and shares it
out-of-band (Discord, a drive). Importing others' files builds a roster;
`NetworkScope` resolves coverage views (who owns what, gaps, single-owner risk)
and can filter the whole app to a single member. No server is involved.

## Build, test, and CI

- **Build:** `dotnet build NexusApp/NexusApp.csproj -c Release`.
- **Tests:** xUnit-style tests in `NexusApp.Tests/` cover the non-UI logic
  (blueprint ownership, Game.log import, network file/store/scope/identity, RSI
  handle parsing, diagnostics).
- **CI:** GitHub Actions verify a clean Release build on every push / PR to
  `main` (`build.yml`), run CodeQL static analysis (`codeql.yml`), and publish
  the installer and portable zip on a version tag (`release.yml`). Dependabot
  keeps NuGet packages and Actions current.
