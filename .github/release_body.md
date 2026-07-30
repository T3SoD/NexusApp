**Nexus __VERSION__** - an unofficial, fan-made mining assistant for Star Citizen.

Both downloads are self-contained, because Nexus includes the .NET runtime. Both downloads:
- need **no admin rights**
- store settings and work orders on your computer
- run offline unless you turn on optional online features (update checks, live market and trading data)

Select the download that you want.

### Screenshots

| | |
|:--:|:--:|
| [<img src="https://raw.githubusercontent.com/T3SoD/NexusApp/main/docs/screenshots/overlay-scan.png" width="420">](https://raw.githubusercontent.com/T3SoD/NexusApp/main/docs/screenshots/overlay-scan.png) | [<img src="https://raw.githubusercontent.com/T3SoD/NexusApp/main/docs/screenshots/blueprint-library.png" width="420">](https://raw.githubusercontent.com/T3SoD/NexusApp/main/docs/screenshots/blueprint-library.png) |
| **Auto-scan overlay** - decodes RS values live over the game | **Blueprint Library** - recipes, ingredients, and unlock missions |
| [<img src="https://raw.githubusercontent.com/T3SoD/NexusApp/main/docs/screenshots/mining-codex.png" width="420">](https://raw.githubusercontent.com/T3SoD/NexusApp/main/docs/screenshots/mining-codex.png) | [<img src="https://raw.githubusercontent.com/T3SoD/NexusApp/main/docs/screenshots/refinery-tracker.png" width="420">](https://raw.githubusercontent.com/T3SoD/NexusApp/main/docs/screenshots/refinery-tracker.png) |
| **Mining Codex** - full resource reference, filterable | **Refinery Tracker** - live countdown timers for jobs |
| [<img src="https://raw.githubusercontent.com/T3SoD/NexusApp/main/docs/screenshots/mission-guides.png" width="420">](https://raw.githubusercontent.com/T3SoD/NexusApp/main/docs/screenshots/mission-guides.png) | [<img src="https://raw.githubusercontent.com/T3SoD/NexusApp/main/docs/screenshots/operations.png" width="420">](https://raw.githubusercontent.com/T3SoD/NexusApp/main/docs/screenshots/operations.png) |
| **Mission Guides** - zoomable maps with the executive hangar countdown | **Operations** - the landing dashboard that links every module |

---

### Option 1 - Installer (`Nexus_Setup.exe`) - *recommended, user friendly*
A guided setup installs Nexus like normal Windows software.

1. Download **`Nexus_Setup.exe`** below.
2. Right-click the file. Select **Properties**. Select the **Unblock** checkbox at the bottom. Click **OK**.
3. Run the file. Follow the prompts. To make a desktop shortcut, select the "Create a desktop shortcut" option.
4. Open Nexus from the Start menu or the desktop.

- The installer makes a Start-menu shortcut and an optional desktop shortcut.
- You can uninstall Nexus cleanly from *Add or remove programs*.
- Nexus installs for the current user under `%LOCALAPPDATA%`. This needs no admin rights.

### Option 2 - Portable (`NexusApp_portable.zip`)
Run Nexus directly. Nexus needs no installation.

1. Download **`NexusApp_portable.zip`** below.
2. Right-click the ZIP file. Select **Properties**. Select the **Unblock** checkbox at the bottom. Click **OK**.
3. Right-click the ZIP file. Select **Extract All…**. Select a location, for example the Desktop or the Documents folder.

   **Caution:** Keep all the files in the folder together.
4. Open the extracted folder. Then open the **NexusApp** folder inside it. Double-click **`NexusApp.exe`**.

- Nexus needs no installation. It writes nothing to the registry.
- Nexus stores settings and work orders under `%APPDATA%\NexusApp`. To remove Nexus completely, delete the app folder and that data folder.
- With update checks on, the portable can install a new version in place and restart. Updates stay off until you say yes.
- You can move Nexus between PCs. You can also run Nexus from a USB stick.

---

> **Windows SmartScreen note (both options):** Nexus is unsigned, so Windows can show a *"Windows protected your PC"* dialog when you start Nexus the first time. Click **More info**. Then click **Run anyway**. Or use the **Unblock** step above. Any Defender flag is a false positive for an unsigned app.

### Features
- **RS Signal Decoder** - enter values manually or use auto-scan to find the resource and the node count
- **Blueprint Library** - search blueprints, see the resources and the unlock contracts, and track what you own; import ownership from an SCMDB export file
- **Blueprint Network** - pool owned-blueprint libraries with your org by trading files, fully offline
- **Mining Codex** - a full resource reference that you can filter by system and by method
- **Refinery Tracker** - live countdown timers for refinery jobs
- **Cargo Hauling** - accepted contracts appear from `Game.log` and consolidate into one route plan
- **Mission Guides** - zoomable maps and tactical guides, with the executive hangar countdown
- **In-game overlay** - six tabs float over the game; ghost mode collapses the overlay to a slim icon rail
- **Live market prices (optional)** - refined sell prices from UEX community data, off by default
- **Commodity Trading (optional)** - a TRADE tab with a capacity-aware route planner, a sell lookup, and a sortable price browser on UEX community data, with an optional SC Trade Tools cross-check; both off by default
- **Update checks (optional)** - opt in once; the portable can install updates itself

---
*Nexus is not affiliated with, endorsed by, or sponsored by Cloud Imperium Games or Roberts Space Industries. Star Citizen®, Roberts Space Industries® and Cloud Imperium® are registered trademarks of Cloud Imperium Rights LLC.*
