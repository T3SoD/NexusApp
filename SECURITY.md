# Security Policy

Nexus is an offline, open-source desktop app. This document explains how it is
built to keep risk low, what it can and cannot touch on your system, and how to
report a vulnerability.

## Reporting a vulnerability

Please report security issues **privately**. Do not open a public issue for a
suspected vulnerability.

- Preferred: use GitHub's **"Report a vulnerability"** button under the
  [Security tab](https://github.com/T3SoD/NexusApp/security) of this repository
  (GitHub private vulnerability reporting).
- Alternatively, reach out through the project's Discord.

Please include the app version (shown in-app and on the release), your Windows
version, clear reproduction steps, and the impact you observed. Reports are
typically acknowledged within a few days, and you will be kept updated as the
issue is investigated. Please hold off on public disclosure until a fix has
shipped.

## Supported versions

Nexus ships as a single latest release. Security fixes land in the next release;
older versions are not patched in place. Always update to the
[latest release](https://github.com/T3SoD/NexusApp/releases/latest).

| Version        | Supported |
|----------------|-----------|
| Latest release | Yes       |
| Older releases | No        |

## Security model: what Nexus can and cannot do

Nexus is built around a deliberately small attack surface. No software can be
called "vulnerability-free," so instead the app limits what it is *able* to do
in the first place:

- **Fully offline.** Nexus makes no network calls. There is no auto-update, no
  telemetry, and no account. You can confirm this with a firewall.
- **No elevation.** It installs and runs per-user and never requests admin
  rights.
- **Reads the screen, not the game.** Auto-scan uses the standard Windows
  screen-capture and OCR APIs. Nexus never reads Star Citizen's memory and never
  injects code, DLLs, or hooks into the game process.
- **No game files modified.** Reference data is bundled with the app. The
  optional Session Tracking feature *reads* the game's plain-text `Game.log`
  (and its rotated backups) read-only, opened shared so it never locks them. The
  Blueprint Network feature additionally reads your RSI handle from that log
  (read-only) to pre-fill an export; you can use a nickname instead.
- **Your data stays local.** Settings, work orders, and your blueprint library
  are stored on your PC. Nothing is shared unless you deliberately export a file
  and hand it to someone yourself.
- **No personal data collected.** Diagnostic logs record app events only (no
  window titles and no game content), and they self-rotate.

## How the code is checked

- **Open source.** The entire codebase, including the OCR pipeline, is in this
  repository. You are encouraged to read it.
- **CI build verification** on every push and pull request to `main`.
- **CodeQL** static security analysis for the C# code.
- **Dependabot** keeps third-party dependencies and CI actions patched.

## Dependencies

Nexus keeps its dependency footprint minimal:

- `Microsoft.Data.Sqlite` — local database access.
- `CommunityToolkit.Mvvm` — UI (MVVM) plumbing.

Both are widely used Microsoft / .NET Foundation packages.
