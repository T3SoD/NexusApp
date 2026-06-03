# Nexus — Star Citizen Mining Assistant

Nexus is a lightweight Windows desktop companion for mining in **Star Citizen**. It decodes RS (Radioactive Signal) scan values into the resource and node count they represent, tracks refinery jobs, and gives you a fast, searchable reference for resources and crafting blueprints — all from an overlay that floats over your game.

> **Disclaimer**
> Nexus is an **unofficial, fan-made tool**. It is **NOT** affiliated with, endorsed by, or sponsored by Cloud Imperium Games (CIG) or Roberts Space Industries (RSI). Star Citizen is a trademark of CIG.
>
> Nexus reads pixel data from your screen and serves reference information from a **local database only**. It does **not** read game memory, inject code, or modify game files. It is **EAC-safe** (Easy Anti-Cheat compatible) and runs entirely outside the game process.

## Features

| Page | What it does |
|------|--------------|
| **RS Signal Decoder** | Manually enter or **auto-scan** an RS value to identify the resource and node count. |
| **Blueprint Library** | Search ship / weapon / armor blueprints and see the raw resources each one requires. |
| **Mining Codex** | Full reference table of all mineable resources, filterable by system (Stanton / Pyro / Nyx) and method (Ship / ROC / FPS). |
| **Refinery Tracker** | Track active refinery jobs with live countdown timers and status indicators. |

**Highlights**

- **Auto-scan overlay** — draw a region over the RS value on your screen and Nexus reads it automatically using the native Windows OCR engine.
- **Floating overlay** that sits over the game and can be repositioned and dimmed to taste.
- **Shopping list** — add resources or blueprint ingredients and have them highlighted in scan results and history.
- **Persistent work orders** — refinery timers survive app restarts.
- Fully **offline** — no account, no internet connection required. Settings and work orders are stored locally on your PC.

## Installation (end users)

1. Download the latest release (ZIP) from the [Releases](../../releases) page.
2. Right-click the ZIP → **Properties** → check **Unblock** at the bottom → **OK** (avoids the SmartScreen prompt).
3. Right-click the ZIP → **Extract All…** and choose a location.
4. Open the extracted folder and run **`Nexus_v4.exe`**.

No installation required.

> **Windows SmartScreen note:** the app is unsigned (code-signing certificates cost several hundred dollars a year), so Windows may show a blue *"Windows protected your PC"* dialog on first run. Click **More info → Run anyway**, or use the **Unblock** step above. If Defender flags it, that's a false positive for an unsigned app.

## Building from source

**Requirements**

- Windows 10 (build 17763 / version 1809) or later
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

**Build & run**

```powershell
git clone https://github.com/T3SoD/NexusApp.git
cd NexusApp
dotnet build
dotnet run --project Nexus_v4
```

**Publish a self-contained release build**

```powershell
dotnet publish -c Release
```

The output lands in `Nexus_v4/bin/x64/Release/net8.0-windows10.0.17763.0/win-x64/publish/`. An [Inno Setup](https://jrsoftware.org/isinfo.php) script (`nexus_installer.iss`) is included to package an installer.

## Tech stack

- **C# / .NET 8** with **WPF** (Windows-only, self-contained `win-x64` build)
- **CommunityToolkit.Mvvm** for MVVM
- **Microsoft.Data.Sqlite** for local storage
- **Windows.Media.Ocr** (native WinRT OCR engine) for the auto-scan feature

## Project layout

```
Nexus_v4/
├─ Nexus_v4.sln
├─ nexus_installer.iss          # Inno Setup installer recipe
└─ Nexus_v4/
   ├─ Assets/                   # Icons and logos
   ├─ Converters/               # WPF value converters
   ├─ Data/seed_data.json       # Bundled mining/blueprint reference data
   ├─ Models/                   # Domain models
   ├─ Services/                 # OCR, scanning, data, settings
   ├─ ViewModels/               # MVVM view models
   ├─ Views/                    # Windows, dialogs, overlay
   └─ Themes/                   # Game-styled WPF theme
```

## Support

For bugs or feedback, contact **TurboV1RG1N**.

## License

Released under the [MIT License](LICENSE).
