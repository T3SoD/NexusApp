# Security Policy

Nexus is an offline, open-source desktop app. This document explains three things:

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

Nexus has one release only: the latest release. Security fixes go into the next release. The project does not patch older releases in place. Always update to the [latest release](https://github.com/T3SoD/NexusApp/releases/latest).

| Version        | Supported |
|----------------|-----------|
| Latest release | Yes       |
| Older releases | No        |

## Security model: what Nexus can and cannot do

Nexus has a small attack surface by design. Because no software is free of all vulnerabilities, Nexus limits what it can do from the start:

- **Fully offline.** Nexus makes no network calls. There is no auto-update, no telemetry, and no account. You can confirm this with a firewall.
- **No elevation.** Nexus installs and runs per user. Nexus never asks for admin rights.
- **Reads the screen, not Star Citizen's memory.** Auto-scan uses the standard Windows APIs for screen capture and OCR. Nexus never reads Star Citizen's memory. Nexus never injects code, DLLs, or hooks into the Star Citizen process.
- **No changes to game files.** Nexus includes the reference data. Nexus reads some Star Citizen files, but it never changes them:
  - The Session Tracking feature is optional. Session Tracking reads the plain-text `Game.log` file of Star Citizen and its rotated backups. Session Tracking reads these files as read-only. Session Tracking opens the files in shared mode and never locks them.
  - Session Tracking can also read the `global.ini` localization file of Star Citizen as read-only. Session Tracking uses `global.ini` to change mod-renamed blueprint names back to their official names.
  - The Blueprint Network feature also reads your RSI handle from `Game.log` as read-only. Blueprint Network uses your RSI handle to pre-fill an export. You can use a nickname instead of your RSI handle.
- **Your data stays local.** Nexus stores your settings, work orders, and blueprint library on your PC. Nexus shares nothing unless you export a file yourself and give it to someone.
- **No personal data collected.** Diagnostic logs record app events only. These logs record no window titles and no game content. These logs self-rotate.

## How the project checks the code

- **Open source.** This repository contains the entire codebase. This includes the OCR pipeline. The project welcomes you to read the code.
- **CI build verification** runs on every push and pull request to `main`.
- **CodeQL** runs static security analysis on the C# code.
- **Dependabot** keeps the third-party dependencies and CI actions patched.

## Dependencies

Nexus uses only a small set of dependencies:

- `Microsoft.Data.Sqlite` gives access to the local database.
- `CommunityToolkit.Mvvm` gives the MVVM support code for the UI.

Both are widely-used packages from Microsoft and the .NET Foundation.
