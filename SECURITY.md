# Security Policy

Nexus is an offline-by-default, open-source desktop app. This document explains three things:

- how the design of Nexus keeps risk low
- what Nexus can and cannot touch on your system
- how to report a vulnerability

## How to report a vulnerability

Report a vulnerability in private.

Caution: Do not open a public issue for a vulnerability that you suspect.

To report a vulnerability, use one of these two methods:

- Preferred method: Use GitHub's **"Report a vulnerability"** button. You find this button on the [Security tab](https://github.com/T3SoD/NexusApp/security) of this repository. This button uses the private vulnerability reporting feature of GitHub.
- Other method: Contact the project through its Discord.

Include this information in your report:

- the app version (you can see it in Nexus and on the release)
- your Windows version
- clear steps to reproduce the vulnerability
- the impact that you saw

The project usually acknowledges reports in a few days. The project keeps you updated while it investigates the vulnerability. Do not tell the public about the vulnerability before the project ships a fix.

## Supported versions

Nexus has one release only: the latest release. Security fixes go into the next release. The project does not patch older releases in place. Always update to the [latest release](https://github.com/T3SoD/NexusApp/releases/latest). If you enable update checks, Nexus can find that release for you and offer to download it. You can always update by hand from the releases page instead.

| Version        | Supported |
|----------------|-----------|
| Latest release | Yes       |
| Older releases | No        |

## Security model: what Nexus can and cannot do

Nexus has a small attack surface by design. Because no software is free of all vulnerabilities, Nexus limits what it can do from the start:

- **Offline by default.** Nexus makes no network calls out of the box. Two features are opt-in and network-capable: update checks and live market prices. Nothing is contacted for either one unless you enable it (or click a manual action yourself: Check now for updates, Refresh now for market data), and you can confirm that with a firewall. There is no telemetry and no account for either feature.
- **Update checks.** Opt-in: nothing is contacted unless you enable them or click Check now yourself. With checks enabled, Nexus contacts GitHub only (github.com and GitHub's own file-download host), fetches the latest version number and release files, and sends nothing about you or your data beyond the connection itself (GitHub sees your IP address, as with any web request). The toggle lives in Settings > Updates.
- **Live market prices.** Opt-in: nothing is contacted unless you enable them yourself, through the one-time consent prompt or the toggle in Settings > Updates, in the Market data section. With this feature enabled, Nexus makes anonymous, read-only GET requests to UEX's community API at `api.uexcorp.uk`, about once an hour while Nexus is open, to fetch sell prices for ores and refined goods. The requests carry no account and no key, and send nothing about you: only a `NexusApp-Market/<version>` user agent identifies the app itself, the same way a browser identifies itself to a website. Nexus requires the connection to be HTTPS and caps how much a response can hold. Prices are cached locally in `%AppData%\NexusApp\cache`, so Nexus still shows the last fetched prices when it is offline. UEX prices are community-reported and can be stale; Nexus shows the age of each price rather than presenting it as guaranteed-current.
- **Signed updates.** Update information is a manifest signed with a key held offline by the maintainer. The app verifies the signature and the file hashes before anything is installed, refuses downgrades, and asks you before every download and every install. A compromised download source or repository cannot make Nexus install anything: at worst, updates stop appearing.
- **Portable self-update.** The portable version can install an update itself. Nexus does the update while it is open, and then restarts as the new version. No helper program runs, and no script runs. The signed manifest format does not change. Nexus does the update in this order:
  - Nexus verifies the download against the signed manifest.
  - Nexus opens the download one time and verifies it again on that open file handle. So the bytes that Nexus checks are the bytes that Nexus unpacks.
  - Nexus unpacks the download, and then verifies each file again just before it puts that file in place.
  - Nexus records each rename in a journal before that rename happens.
  - Nexus replaces each file in the update with a rename. Nexus keeps each previous file as a `.old` file.
  - Nexus restarts as the new version.
- **Update recovery.** If an update stops part way, the next start puts the previous version back from the kept `.old` files. Nexus keeps the previous files and the verified download until the new version starts. The first start of the new version removes the `.old` files and the download.
- **Safe conditions for the self-update.** Nexus tries the self-update only when the conditions are safe. Nexus does not try it in a protected folder, on a network drive, or with a second Nexus window open. Nexus then offers a manual update. When you choose it, Nexus verifies and unpacks the update and opens the new folder and your current folder. You then do one copy.
- **No elevation.** Nexus installs and runs per user. Nexus never asks for admin rights.
- **Reads the screen, not Star Citizen's memory.** Auto-scan uses the standard Windows APIs for screen capture and OCR. Nexus never reads Star Citizen's memory. Nexus never injects code, DLLs, or hooks into the Star Citizen process.
- **No changes to game files.** Nexus includes the reference data. Nexus reads some Star Citizen files, but it never changes them:
  - Session Tracking runs on every launch. There is no user toggle to turn it off. Session Tracking reads the plain-text `Game.log` file of Star Citizen and its rotated backups. Session Tracking reads these files as read-only. Session Tracking opens the files in shared mode and never locks them.
  - The hauling tracker and the shard tracker also read `Game.log`. Both run on every launch. Both read `Game.log` as read-only in shared mode.
  - Session Tracking can also read the `global.ini` localization file of Star Citizen as read-only. Session Tracking uses `global.ini` to change mod-renamed blueprint names back to their official names.
  - The Blueprint Network feature also reads your RSI handle from `Game.log` as read-only. Blueprint Network uses your RSI handle to pre-fill an export. You can use a nickname instead of your RSI handle.
- **Your data stays local.** Nexus stores your settings, work orders, and blueprint library on your PC. Nexus shares nothing unless you export a file yourself and give it to someone.
- **Safe import of shared files.** Another player can share a `.nexuslib` library file with you. Nexus limits the size of this file before it reads the file. Nexus also limits the member count and the blueprint count in the file. Nexus cleans the text from the file before the text goes to the log.
- **Safe import of SCMDB export files.** The Blueprint Library can import an SCMDB export file. Nexus limits the size of this file before it reads the file. A bad file cannot crash the import; Nexus shows an error instead. The import only adds ownership marks, and only after you confirm the preview.
- **No personal data collected.** Diagnostic logs record app events only. These logs record no window titles and no game content. These logs self-rotate.

## How the project checks the code

- **Open source.** This repository contains the entire codebase. This includes the OCR pipeline. The project welcomes you to read the code.
- **CI build and test** runs on every push and pull request to `main`. The build compiles the app and runs the full unit test suite. The release workflow runs the same test suite before it publishes a release.
- **CodeQL** runs static security analysis on the C# code.
- **Dependabot** keeps the third-party dependencies and CI actions patched.

## Dependencies

Nexus uses only a small set of dependencies:

- `Microsoft.Data.Sqlite` gives access to the local database.
- `CommunityToolkit.Mvvm` gives the MVVM support code for the UI.
- `Microsoft.Web.WebView2` shows one beta 3D view. The view loads only bundled local content and blocks all external navigation.

All three are widely-used packages from Microsoft and the .NET Foundation.
