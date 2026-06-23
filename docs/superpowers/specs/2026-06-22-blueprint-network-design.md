# Blueprint Network — Design Spec

Status: draft for review · Branch: `feature/blueprint-network` · Date: 2026-06-22

## 1. Summary

A new top-level **Blueprint Network** tab that lets Nexus users share their owned-blueprint
libraries with each other (offline, by exchanging files) and see, for any blueprint, **who in
their network owns it**. It is the shared/social counterpart to the existing single-player
**Blueprint Library** tab.

The app never touches the network — users move files (export → share on Discord/drive → import).
This preserves Nexus's "fully offline, no network code" design.

## 2. Non-goals (YAGNI)

- No server, sync, accounts, or auto-update. Offline file exchange only.
- No merging other people's blueprints into your own owned list (members stay distinct).
- No changes to the existing Blueprint Library's user-facing behavior (it only gets refactored
  internally to share a list control — see §9).
- No in-game overlay surfacing of network data in v1.
- No "who can build this ship" craft-coordinator, activity feed, or wishlist in v1 (possible later).

## 3. Decisions locked during design

- **Model:** a roster of *members*, each a named profile with their own owned-blueprint set,
  keyed by blueprint **name** (case-insensitive), exactly like today's ownership.
- **Transport:** offline export/import of files. A *coordinator* can import many individual files
  and re-export one **combined roster** for a large org, so rank-and-file members do a single import.
- **Organization:** user-defined **groups** (a member can be in several; "Friends" and an org are
  just groups you create). A group is "a name + a list of members."
- **Scale:** designed for 100+ members. Ownership is shown as **counts + a coverage bar**, never a
  wall of avatars; faces appear only when a row is expanded, and are searchable.
- **Identity:** a per-user **GUID** is the stable identity across file versions; a **display label**
  (RSI handle or a nickname, chosen per export) is cosmetic. RSI handle is auto-detected from
  Game.log. See §6.
- **Architecture:** Method **B** — Library and Network are two tabs that **share one blueprint-list
  control**; they differ only in the per-row right-hand content (owned toggle vs coverage cell).
- **Theme:** classic/teal palette. Nav icon = the standard 3-node "share" glyph (a vector `Path`,
  like the Settings gear).

## 4. Data model

A **Member**: `Id` (GUID), `DisplayName`, `IdentityKind` (`handle` | `nickname`),
`RsiHandle` (nullable), `LastUpdatedUtc`, `IsSelf` (bool).

A **Group**: `Id` (GUID), `Name`, `CreatedUtc`. Many-to-many with members.

**Ownership**: per member, a set of owned blueprint names (raw strings, case-insensitive).

"**Self**" is the local user. Self's ownership is **not duplicated** — it is read live from the
existing `AppSettings.OwnedBlueprints` (the single source of truth). Coverage math unions self
(from settings) with the other members (from the network store). This avoids a sync class of bugs.

## 5. Storage

Network data lives in its **own SQLite file**: `%AppData%\NexusApp\network.db`.

Rationale: it must NOT go in `settings.json` (parsed on every settings load) and must NOT go in
`nexus.db` (which is reseeded on every app update, per `SettingsService`). A separate db survives
updates and indexes well for 100 members × ~600 blueprints (~60k rows). Uses the existing
`Microsoft.Data.Sqlite` dependency.

Schema:

```sql
CREATE TABLE members (
  id            TEXT PRIMARY KEY,      -- GUID
  display_name  TEXT NOT NULL,
  identity_kind TEXT NOT NULL,         -- 'handle' | 'nickname'
  rsi_handle    TEXT,                  -- nullable; present only if shared as handle
  last_updated  TEXT NOT NULL,         -- ISO-8601 UTC
  is_self       INTEGER NOT NULL DEFAULT 0
);
CREATE TABLE member_blueprints (
  member_id      TEXT NOT NULL,
  blueprint_name TEXT NOT NULL,
  PRIMARY KEY (member_id, blueprint_name)
);
CREATE INDEX ix_mb_blueprint ON member_blueprints(blueprint_name);  -- "who owns X" + counts
CREATE TABLE network_groups (   -- "groups" is near-reserved in SQLite, so the table is network_groups
  id          TEXT PRIMARY KEY,
  name        TEXT NOT NULL,
  created_utc TEXT NOT NULL
);
CREATE TABLE group_members (
  group_id  TEXT NOT NULL,
  member_id TEXT NOT NULL,
  PRIMARY KEY (group_id, member_id)
);
CREATE INDEX ix_gm_group  ON group_members(group_id);
CREATE INDEX ix_gm_member ON group_members(member_id);
```

Service: `NetworkStore` (new) wraps all reads/writes. Pragmas: WAL, busy_timeout (match existing
db hardening).

## 6. Identity & RSI-handle detection

**Local identity** (added to `AppSettings`):
- `LocalNetworkId` (GUID string, generated once on first use).
- `LocalDisplayName` (string).
- `LocalIdentityKind` (`handle` | `nickname`).
- `DetectedRsiHandle` (string, from Game.log; reference/default only).

The network data itself is NOT in settings — only this small identity block.

**Handle detection from Game.log** (confirmed present in a real LIVE log, 2026-06-22):
- Primary token — the legacy login response line:
  `<Legacy login response> [CIG-net] User Login Success - Handle[<HANDLE>] - Time[...]`
  → extract between `Handle[` and `]`.
- Fallback token — the character status line:
  `<AccountLoginCharacterStatus_Character> Character: ... - name <HANDLE> - state STATE_CURRENT`
  → extract between `- name ` and ` - state`.
- A third corroborator (`nickname="<HANDLE>"`) appears throughout the Network lines as a
  mid-session safety net.

Detection capture **only the handle** — never the `accountId`/`geid` on the same lines.

Mechanics: Game.log is rewritten per launch and is ~1 MB, so on startup / when the path is known,
scan the current file for the primary token (fallback to secondary), cache into
`DetectedRsiHandle`; keep watching login lines for re-logins/character switches. Hook point:
`GameLogSession.Ingest` / the `GameLogWatcher` line handler; `LogCategory.Login` already exists.
If no handle is found, export falls back to manual entry.

## 7. File format

Extension: `.nexuslib`. Content: UTF-8 JSON, human-readable, version-stamped.

Single-member library export:
```json
{
  "schema": 1,
  "kind": "library",
  "exportedAtUtc": "2026-06-22T00:00:00Z",
  "app": "NexusApp 5.5.0",
  "member": {
    "id": "<guid>",
    "name": "PlayerName",
    "identityKind": "handle",
    "rsiHandle": "<HANDLE-or-null>",
    "updatedAtUtc": "2026-06-22T00:00:00Z",
    "ownedBlueprints": ["Bracket Cooler", "FR-86 Shield"]
  }
}
```

Combined roster export (coordinator): `"kind": "roster"`, optional `"groupName"`, and a
`"members": [ ... ]` array of the same member shape. Same schema otherwise.

Service: `NetworkFileService` (new) — `ExportSelf`, `ExportRoster`, `ImportFile` (handles both kinds).

## 8. Import / export behaviour

**Import matching** (per incoming member), in order:
1. Match by `id` (GUID) → update that member in place.
2. Else, if `identityKind == handle` and `rsiHandle` present → match by `rsiHandle`
   (case-insensitive). Covers a member who reinstalled and lost their GUID.
3. Else → new member. If the display name collides with an existing *different* id, the import
   dialog asks: *"New person, or merge into [existing]?"* — never a silent duplicate.

Newer wins: if the incoming `updatedAtUtc` is **strictly** newer, replace that member's owned set; otherwise keep the existing one (equal timestamps keep what's stored).

**Unrecognized blueprints:** a member may own a blueprint name not in the local seed (cross-patch
drift). These are **stored as-is** and still count toward that member; in the UI they group under
"Unrecognized (n)" rather than being dropped. (Mirrors the existing `UnrecognizedBlueprintReport`
philosophy.)

**Group assignment on import:** a roster's `groupName` auto-creates/assigns that group; for a single
library import, the dialog lets the user pick/create a group (default: ungrouped / "Friends").

**Export:** self only (a `library`) or the whole current view (a `roster`). At export the user picks
the identity label: **RSI handle** (pre-filled from `DetectedRsiHandle`) or a **nickname**.

## 9. UI

New nav `RadioButton` "BLUEPRINT NETWORK" (`Tag="network"`), two-line label, share-icon `Path`
glyph; new `PageNetwork` content region. The existing Library tab is unchanged for the user.

`PageNetwork` layout:
- Header: title, member-avatar cluster, **Import** + **Export** buttons.
- Sub-tabs: **Overview** · **Blueprints** · **Members**.
- **Group switcher** chips (All + each group + "New group"); selection filters all sub-tabs.
- **Overview:** coverage band (covered/total %, "nobody owns" count, "single owner" count),
  per-member coverage cards, single-owner watch list.
- **Blueprints:** coverage band + filters (All / Nobody owns / Single owner / I'm missing) + the
  shared blueprint list, each row a coverage cell (micro-bar + `n / N`, colored for full/single/none);
  expanding a row shows a **searchable owner list** + a "show the N missing" toggle.
- **Members:** list (name, identity kind, last-updated, groups), remove, assign-to-group.

**Shared list engine (Method B):** extract the Blueprint Library's drill-down/list rendering
(currently in `MainWindow.xaml.cs` — `RenderBlueprintNav`, `EnterCategory`, etc.) into a reusable
control that takes (a) a data/category source, (b) a filter predicate, and (c) a per-row
right-adornment delegate. Library supplies the owned-toggle adornment; Network supplies the
coverage-cell adornment. Built once, hosted in both tabs. This is also the long-pending
"split the Library code-behind" cleanup.

## 10. Coverage computation

For a selected group (or All): for each blueprint, `ownerCount = COUNT(distinct members in scope who
own it)`, `total = members in scope`. Self included from `AppSettings.OwnedBlueprints`. Derived:
covered = ownerCount > 0; nobody-owns = ownerCount == 0; single-owner = ownerCount == 1.
Indexed `member_blueprints(blueprint_name)` keeps this fast at scale.

## 11. Logging (App Log Monitor)

New `[NET]` tag into `nexus.log` (per the standing "log every feature" rule):
- `[NET] import done: +X new, Y updated, Z matched, W unrecognized`
- `[NET] import error: <reason>` for bad/corrupt/incompatible files
- `[NET] export: kind=library, identity=handle|nickname, members=N`
- `[NET] member removed (count now N)` — **counts only, never names/handles** (no filenames either)
- `[NET] group created` / `[NET] member group membership updated` — **no group or member names**
- `[NET] local RSI handle detected from Game.log` — **never the handle value**

Plus `[UI]` entries (via `InteractionLog`) for Network nav, sub-tab switches, group switch, filter
chips, Import/Export clicks — following the existing `[TAG] message @ Window/Region` format.

Privacy: the diagnostic snapshot is user-shareable, so **third-party display names/handles are never
written to the log** — only counts and operation types.

## 12. Privacy & disclosure

- Reading the RSI handle from Game.log **reverses a deliberate prior choice** (`UnrecognizedBlueprintReport`
  explicitly avoided capturing the handle). This is intentional and must be disclosed.
- Update About → Legal and README: Nexus reads your RSI handle from Game.log (read-only) to pre-fill
  exports; sharing is opt-in per export (handle or nickname); exported files contain only what you choose.
- The detected handle is runtime-only user data: stored in the user's local settings, shared only on
  an opt-in export, and never written to logs/snapshots. (It must never appear in the repo, this spec,
  memory, or any commit.)
- EAC-safe: Game.log read remains read-only, consistent with existing disclosures.

## 13. Error handling

- Corrupt/invalid file → caught; dialog "Couldn't read this file" + reason; `[NET] error` log.
- `schema` newer than supported → "Made by a newer Nexus — update to import."
- Name collision with different id → merge-or-new prompt (§8).
- Unrecognized blueprints → kept, grouped, never dropped (§8).
- Missing/locked Game.log → handle detection silently no-ops; export falls back to manual entry.

## 14. Testing

Headless unit tests (xUnit, matching existing test project):
- File round-trip: export → import → equal.
- Import matching: by GUID; by handle fallback; new member; collision prompt path; newer-wins.
- Combined roster merge (many members, group auto-assign).
- Coverage math: counts, nobody-owns, single-owner, self-included-from-settings.
- Handle parser: primary + fallback sample lines → correct handle; ignores accountId/geid; absence.
- Store survives a simulated `nexus.db` reseed (separate db untouched).
- Group membership (multi-group) + filtering.

## 15. Phased build order (each slice is a compile-able checkpoint)

I cannot compile WPF in WSL; each slice is sized so Zach can build/test it via `test_nexus.ps1`
on Windows before the next.

1. **Storage + model** — `network.db`, `NetworkStore`, schema, `AppSettings` identity fields,
   self-from-settings integration. + unit tests. No UI.
2. **File format + import/export service** — `NetworkFileService`, serialize/deserialize, matching/
   merge, unrecognized handling. + tests. Headless.
3. **Game.log handle detector** — parser + hook + `DetectedRsiHandle` + tests.
4. **UI shell** — new nav tab + share icon, `PageNetwork` scaffold (sub-tabs, group switcher),
   Members sub-tab, Import/Export dialogs wired to the services.
5. **Shared list engine** — refactor the Library list into a reusable control; Network Blueprints
   sub-tab uses it with coverage cells, owner drill-in, filters.
6. **Overview sub-tab** — coverage band, gaps, single-owner, per-member cards.
7. **Logging + disclosure + polish** — `[NET]`/`[UI]` logging, About/README disclosure text.

## 16. Risks

- Game.log token could shift across SC patches — confirmed current; re-verify on major patches;
  graceful no-op if absent.
- The shared-list refactor touches stable, heavily-used Library code — must land with no UX change
  to Library; needs careful Windows testing.
- No local WPF compiler → Windows/CI is the only build gate; the slice plan is the mitigation.
