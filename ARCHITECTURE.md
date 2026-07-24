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
disk in the per-user app-data folder. There is no network code.

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
The Views are the WPF windows and dialogs. The two main surfaces are
**MainWindow** and **OverlayWindow**. **MainWindow** is the full NexusApp.
**OverlayWindow** is a compact panel. **OverlayWindow** stays always-on-top and
floats over the game.

Supporting windows include:
- the region selector for auto-scan
- the toast and scan-indicator popups
- the app-log monitor
- the settings, about, and help dialogs

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
- **GameLogSession / GameLogWatcher / GameLogBlueprintImporter /
  RsiHandleParser** - the read-only `Game.log` subsystem (see below).
- **ComponentStringReference / GlobalIniReader** - translate mod-renamed
  blueprint names back to their official names. They join the user's read-only
  `global.ini` to the bundled `components.ini`. The `Game.log` import path uses
  them.
- **Network (NetworkFileService / NetworkStore / NetworkScope)** - the
  file-exchange subsystem for the offline Blueprint Network (see below).
- **Logger / InteractionLog / ForegroundMonitor / DiagnosticSnapshot** - the
  diagnostics. These are a self-rotating event log, UI-interaction breadcrumbs,
  tracking of the foreground window and process, and the copy/save diagnostic
  bundle. This tracking records the process name only, never window titles.

### Models (`Models/`)
The Models are plain data types: `Blueprint`, `Resource`, `WorkOrder`,
`ShoppingItem`, `AppSettings`, and the `NetworkFile` / `NetworkModels` exchange
types.

## Data and storage

- **Reference data** ships as `Data/seed_data.json`. The build embeds it into
  the assembly as a resource. It is the single source of mining and blueprint
  data. There is no over-the-air update path. A second embedded reference,
  `Data/components.ini`, maps internal component keys to their official names.
  The build refreshes it for each game patch.
- **User data** (settings, work orders, and the owned-blueprint library) is on
  disk in the per-user app-data folder. NexusApp stores it with SQLite and JSON.
- **Blueprint Network** uses a separate local `network.db`. It exchanges
  `.nexuslib` files that the user moves by hand. Nothing syncs automatically.
- **Versioning** comes from one source: the `<Version>` in
  `NexusApp/NexusApp.csproj`. The in-app badges and the installer read it from
  the built executable. For releases, CI overrides it from the git tag.

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

### Session Tracking (Beta, opt-in)
`GameLogWatcher` reads the plain-text `Game.log` of the game as the game adds new
lines. It opens the file read-only. `GameLogBlueprintImporter` finds the
"Received Blueprint" notifications. It marks those blueprints as owned.

A localization mod can rename blueprint names. NexusApp translates these names
back to their official names. It joins the user's read-only `global.ini` to the
bundled `components.ini`. NexusApp finds `global.ini` next to `Game.log`, or the
user sets the path.

`GameLogSession` is an app-lifetime hub. It ties the watcher, the importer, and
the per-session tally together.

### Blueprint Network (offline sharing)
A user exports their owned-blueprint library to a `.nexuslib` file. The user
shares the file out-of-band, for example on Discord or a drive. When the user
imports files from other people, NexusApp builds a roster. `NetworkScope` creates
coverage views (who owns what, gaps, and single-owner risk). `NetworkScope` can
filter all of NexusApp to a single member. No server is involved.

## Build, test, and CI

- **Build:** Run `dotnet build NexusApp/NexusApp.csproj -c Release`.
- **Tests:** xUnit-style tests in `NexusApp.Tests/` cover the non-UI logic. This
  logic is blueprint ownership, Game.log import, the network
  file/store/scope/identity, RSI handle parsing, and diagnostics.
- **CI:** GitHub Actions verify a clean Release build on every push and PR to
  `main` (`build.yml`). On a version tag, GitHub Actions publish the installer
  and the portable zip (`release.yml`). `release.yml` also posts the changelog to
  Discord. GitHub's default code-scanning setup runs CodeQL static analysis, with
  no workflow file. Dependabot keeps the NuGet packages and the Actions current.
