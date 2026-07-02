# Nexus — Star Citizen Companion App

<p align="center">
  <img src="NexusApp/Assets/nexus_logo_classic.png" alt="Nexus logo" width="240">
</p>

**The offline, EAC-safe companion for Star Citizen's mine, refine, craft, haul loop.**

Nexus decodes RS scan values into resource type and node count, times your refinery jobs, and doubles as a searchable reference for resources, blueprints, and which blueprints you own. It reads the game log to auto-collect blueprints the moment you unlock them and to merge your accepted hauling contracts into one consolidated pickup and delivery route, all from an overlay that floats over the game, **fully offline**. The **Blueprint Network** extends ownership tracking to friends or your org: trade library files and everyone sees who owns what.

> **Disclaimer:** Nexus is an unofficial, fan-made tool. It is **not** affiliated with, endorsed by, or sponsored by Cloud Imperium Games (CIG) or Roberts Space Industries (RSI). Star Citizen is a trademark of CIG.

> **EAC-safe by design:** Nexus runs entirely outside Star Citizen - no injection, no memory reading, no game files modified. It only captures your screen (the standard Windows OCR APIs) and reads the plain-text `Game.log` the game writes to disk (read-only, opened shared). It installs per-user, runs fully offline, and the whole pipeline is open source in this repo. There's nothing for Easy Anti-Cheat to flag.

## Features

| Page | What it does |
|------|--------------|
| **Operations** | The landing dashboard: your last scan, refinery queue, cargo in transit, session blueprints, and network coverage at a glance, with links into every module. |
| **RS Signal Decoder** | Manually enter or **auto-scan** an RS value to identify the resource and node count. |
| **Refinery Tracker** | Track active refinery jobs with live countdown timers and status indicators. |
| **Mining Codex** | Full reference table of all mineable resources, filterable by system (Stanton / Pyro / Nyx) and method (Ship / ROC / FPS). |
| **Blueprint Library** | Search ship / weapon / armor / ammo blueprints and see the raw resources each one requires. Mark which blueprints you own and filter by owned / not owned. |
| **Blueprint Network** | Share which blueprints you own with friends or your org by trading library files, and see who in your group owns what — coverage, gaps to farm, and single-owner risk. Fully offline: you exchange files, nothing syncs. |
| **Cargo Hauling** | Hauling contracts you accept in-game appear automatically from `Game.log`, consolidated into collect and deliver stops per location. Optional screen-scan enriches each haul with reward, contractor, and cargo details. |

**Highlights**

- **Auto-scan overlay:** draw a region over the RS value on your screen and Nexus reads it automatically using the native Windows OCR engine.
- **Floating overlay** that sits over the game and can be repositioned and dimmed to taste.
- **Blueprint ownership tracking:** mark which blueprints you own, filter the library by owned / not owned, and track your collection completion per category, so you don't have to check in-game.
- **Session Tracking:** Nexus reads your Star Citizen `Game.log` to mark blueprints Owned automatically the moment you receive them in-game, or imports everything you've already unlocked from past logs. Always on, read-only, and it never writes to or modifies any game file.
- **Cargo Hauling:** accepted contracts appear on their own, with a consolidated collect-and-deliver plan across all active hauls, live shard tracking, and automatic cleanup when you change shards.
- **Guided tour and built-in user guide:** a welcome tour walks new users through the app, replayable anytime from Help, alongside a searchable help guide covering every module.
- **Blueprint Network:** pool your owned-blueprint library with friends or your org by trading files to see group coverage, gaps, and single-owner risk. Full details in the Blueprint Network section below.
- **Shopping list:** add resources or blueprint ingredients and have them highlighted in scan results and history.
- **Persistent work orders:** refinery timers survive app restarts.
- Fully **offline:** no account or internet connection required. Settings and work orders are stored locally on your PC.

## Screenshots

### Operations
The landing dashboard during a live session: last scan decoded, refinery queue with one order ready, cargo in transit, and your current shard - with drill-in links to every module.

[![Nexus Operations dashboard with live KPI cards, refinery queue, active hauls, and server shard panel](docs/screenshots/operations.png)](docs/screenshots/operations.png)

### Auto-scan, in the cockpit
The overlay's SCAN tab riding over Star Citizen while mining: the detection box is drawn around the in-game RS readout, and Nexus decodes it live - **RS 11,700** identified as **Torite** (RS 3,900 x3 nodes, exact), scan history filling in below.

[![Nexus overlay auto-scanning an RS value in the Star Citizen mining cockpit and decoding it live](docs/screenshots/overlay-scan.png)](docs/screenshots/overlay-scan.png)

### Overlay HUB over the game
The HUB tab floating over gameplay: green status lights for session tracking and both scanners, blueprints collected this session, and your current server and shard - all read-only, at a glance.

[![Nexus overlay HUB tab floating over Star Citizen gameplay with status lights and shard panel](docs/screenshots/overlay.jpg)](docs/screenshots/overlay.jpg)

### RS Signal Decoder
Type any RS value for a ranked breakdown: the best match as a hero card with node count and refinery yield, close matches below, and re-runnable scan history on the right.

[![Nexus RS Decoder with a Lindinium exact match hero card and other close matches](docs/screenshots/rs-decoder.png)](docs/screenshots/rs-decoder.png)

### Blueprint Library
Drill into any blueprint for its full bill of materials, the contracts that unlock it, and a ranked WHERE TO MINE plan for the ingredients - and track everything you own.

[![Nexus Blueprint Library showing a blueprint's bill of materials, unlock contracts, and where to mine panel](docs/screenshots/blueprint-library.png)](docs/screenshots/blueprint-library.png)

### Blueprint Network
Group coverage at a glance: the coverage ring, per-member ownership, and a watch list of the gaps nobody owns yet and single-owner blueprints at risk.

[![Nexus Blueprint Network showing group blueprint coverage ring, per-member ownership, and watch list](docs/screenshots/blueprint-network.png)](docs/screenshots/blueprint-network.png)

### Mining Codex
A full reference of every mineable resource: searchable, filterable by star system (Stanton / Pyro / Nyx) and mining method (Ship / ROC / FPS), with a detail panel covering RS value, refinery yields, locations, and the blueprints that use it.

[![Nexus Mining Codex resource list with rarity colors and a Gold detail panel showing refinery yields and locations](docs/screenshots/mining-codex.png)](docs/screenshots/mining-codex.png)

### Refinery Tracker
Live work orders as cards: one ready to collect, one mid-refine with its countdown running. Timers survive app restarts.

[![Nexus Refinery Tracker with a ready work order and a refining order counting down](docs/screenshots/refinery-tracker.png)](docs/screenshots/refinery-tracker.png)

### Cargo Hauling
Two live contracts tracked automatically from `Game.log` with their rewards, plus the consolidated collect / deliver table that turns every leg into one route plan grouped by location.

[![Nexus Cargo Hauling page with live contract cards and the collect deliver consolidation table](docs/screenshots/cargo-hauling.png)](docs/screenshots/cargo-hauling.png)

### Overlay HAULING tab
The same haul plan in-game: totals, consolidated stops, and per-contract progress without leaving the pilot seat.

[![Nexus overlay HAULING tab with haul totals, consolidated stops, and contract progress](docs/screenshots/overlay-hauling.png)](docs/screenshots/overlay-hauling.png)

## Installation (end users)

Nexus ships two ways; pick whichever suits you. Both are self-contained (the .NET runtime is bundled), need **no admin rights**, store settings and work orders locally, and run fully offline.

### Option 1 — Installer (`Nexus_Setup.exe`) — *recommended, user friendly*

A guided setup that installs Nexus like normal Windows software.

1. Download **`Nexus_Setup.exe`** from the [Releases](../../releases) page.
2. Right-click it → **Properties** → check **Unblock** at the bottom → **OK**.
3. Run it and follow the prompts (optionally tick "Create a desktop shortcut").
4. Launch Nexus from the Start menu or desktop.

### Option 2 — Portable (standalone `NexusApp.exe`)

Run the app directly, with no installation.

1. Download **`NexusApp_portable.zip`** from the [Releases](../../releases) page.
2. Right-click the ZIP → **Properties** → check **Unblock** at the bottom → **OK**.
3. Right-click the ZIP → **Extract All…** and choose a location (Desktop or Documents is fine).
4. Open the extracted folder and double-click **`NexusApp.exe`** (keep the whole folder together).

> **Windows SmartScreen note (applies to both options):** the app is unsigned (code-signing certificates cost several hundred dollars a year), so Windows may show a blue *"Windows protected your PC"* dialog on first run. Click **More info → Run anyway**, or use the **Unblock** step above. If Defender flags it, that's a false positive for an unsigned app.

<details>
<summary><strong>For developers — tech stack & project layout</strong></summary>

**Tech stack**

- **C# / .NET 8** with **WPF** (Windows-only, self-contained `win-x64` build)
- **CommunityToolkit.Mvvm** for MVVM
- **Microsoft.Data.Sqlite** for local storage
- **Windows.Media.Ocr** (native WinRT OCR engine) for the auto-scan feature

**Project layout**

```
NexusApp/
├─ NexusApp.sln
├─ nexus_installer.iss          # Inno Setup installer recipe
└─ NexusApp/
   ├─ Assets/                   # Icons and logos
   ├─ Converters/               # WPF value converters
   ├─ Data/seed_data.json       # Bundled mining/blueprint reference data
   ├─ Models/                   # Domain models
   ├─ Services/                 # OCR, scanning, data, settings
   ├─ ViewModels/               # MVVM view models
   ├─ Views/                    # Windows, dialogs, overlay
   └─ Themes/                   # Game-styled WPF theme
```

</details>

## Support & Feedback

Nexus is built by one person for the mining community, and hearing from people who use it is the best part. If you enjoy the app, please reach out and say so.

Got a bug, a feature idea, or want to share how Nexus is working for you? **Message T3SoD on Discord** or **[open an issue on GitHub](https://github.com/T3SoD/NexusApp/issues)**. All feedback is welcome and helps shape where Nexus goes next.

When reporting a bug, you can attach a diagnostic snapshot: open the **Settings** module at the bottom of the app dock → **Diagnostics** → **Open App Log Monitor**, then **Save snapshot** to bundle your app version, OS, and recent log into a single file.

## License

Released under the [MIT License](LICENSE).
