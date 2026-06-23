# Blueprint Network — Individual Person Filter — Design Spec

Status: draft for review · Branch: `feature/blueprint-network` · Date: 2026-06-22

Extends [the Blueprint Network spec](2026-06-22-blueprint-network-design.md). No change to
export/import, storage, identity, or data model — this is purely a new *view scope*.

## 1. Summary

Add the ability to scope the Blueprint Network to a **single member** (a username that is not the
local user), via a **"Person" dropdown** in the scope bar. When a person is selected, all three
Network tabs (Overview / Blueprints / Members) show **only that person, with the local user
excluded**. Clearing returns to the existing group/All scope.

## 2. Problem

Today the scope bar offers only **All** or a **group**, and coverage math **always counts the
local user on top** (`total = scopeIds.Count + 1`, comment: "Self is always counted on top"). There
is no way to look at exactly one other person — e.g., "what does Dave own / what is Dave missing."

## 3. Decisions locked during design

- **Entry point:** a **"Person" dropdown** in the scope bar (chosen over scope chips per-member or
  clicking a member row). Lists **All** + every member's display name, independent of the active
  group chip.
- **Breadth:** selecting a person **scopes all three tabs** to them (not Blueprints-only) until
  cleared.
- **Self:** when a person is selected, the **local user is excluded** — scope is exactly that one
  member.
- **Overview when scoped to one person:** the **"only a single person holds it" callout is
  suppressed** (meaningless for a one-person scope). Overview then reads as that person's personal
  coverage: owned count / catalog %, plus what they're missing.
- **Out of scope:** filtering to yourself; selecting multiple people at once.

## 4. State & precedence

- New field on `NetworkPage`: `_personFilter` (`string?` member id, `null` = none).
- A non-null `_personFilter` **overrides** `_groupFilter`.
- Clearing `_personFilter` — pick **All** in the dropdown, or click any group/All chip — restores
  the prior group scope.

## 5. Scope resolution — extracted & testable

Today each tab independently computes `scopeIds` from `_groupFilter` and hard-codes self-on-top
(`+ 1`). Extract this into one **pure resolver** so the new exclude-self logic gets unit coverage —
`NetworkPage` is a view and is not unit-tested today, but a pure function is.

Proposed shape (no UI/db dependencies):

```
NetworkScope Resolve(string? personFilter,
                     string? groupFilter,
                     IReadOnlyList<string> groupOrAllMemberIds)
// returns { IReadOnlyList<string> ScopeIds; bool IncludeSelf; string? FocusPersonId; }
```

- `personFilter != null` → `ScopeIds = [personFilter]`, `IncludeSelf = false`, `FocusPersonId = personFilter`
- else → `ScopeIds = groupOrAllMemberIds`, `IncludeSelf = true`, `FocusPersonId = null`

The three tab builders call this helper instead of their inline logic. Coverage denominators become
`ScopeIds.Count + (IncludeSelf ? 1 : 0)` instead of the hard-coded `+ 1`.

## 6. UI — Person dropdown

- **Location:** right end of the scope chip-bar row (`_groupBar`), aligned opposite the chips.
- **Control:** a themed dropdown — `Person: [ All ▾ ]`. Implementation is a ComboBox styled to the
  dark theme, or a button+popup if ComboBox theming proves awkward (decide in the plan).
- **Contents:** **All** (default) + each member's `DisplayName`, ordered like the Members list.
- **On select a person** → set `_personFilter`, refresh the active tab in place (**no
  auto-navigation** — whatever tab you're on simply re-scopes). The dropdown's selected value is the
  active-person indicator.
- **Clear** → select **All** in the dropdown, or click any group/All chip. An optional `[✕]` beside
  the dropdown is a shortcut for the same clear.
- **Empty state:** if there are no members, the dropdown is hidden (nothing to filter to).

## 7. Per-tab behavior when a person is selected (self excluded)

- **Blueprints:** list scoped to that member; the owned / missing / all filters operate on them alone.
- **Members:** just that member's row.
- **Overview:** that member's personal coverage — owned count / catalog %, and their missing list.
  The **"only a single person holds it" callout is suppressed**, and the per-member cards section
  shows just them.

## 8. Logging

Per the logging-on-every-feature rule, emit a `[NET]` line on person select and clear:
`[NET] scope: person=<name>` and `[NET] scope: person cleared`. Match the level of the existing group
switch.

## 9. Testing

New unit tests for the pure scope resolver (`NetworkScopeTests`):

- person set → `ScopeIds == [person]`, `IncludeSelf == false`
- person null + group → `ScopeIds == group members`, `IncludeSelf == true`
- person null + no group → `ScopeIds == all`, `IncludeSelf == true`
- person filter overrides group filter (both set → person wins)
- denominator helper respects `IncludeSelf`

Views remain not unit-tested; the resolver carries the logic that matters. (Reminder: WPF can't be
built in WSL — CI on push is the compile gate.)

## 10. Files touched

- `NexusApp/Views/NetworkPage.cs` — add `_personFilter`; add the dropdown UI; call the resolver from
  the three tab builders; suppress the Overview single-owner callout when focused; logging.
- **New** `NexusApp/Services/NetworkScope.cs` (or equivalent pure static) — the resolver.
- **New** `NexusApp.Tests/NetworkScopeTests.cs` — resolver tests.
- Reuse existing `NetworkStore.GetMembers()` / `GetGroupMemberIds()` for the member lists.

## 11. Edge cases

- **Selected person removed** (Remove button, or absent after a re-import) → clear `_personFilter`
  back to All before rendering.
- **Switching groups while a person is selected** → person takes precedence; clearing returns to the
  (possibly changed) group.
- **Duplicate display names** → the dropdown keys by member **Id** and shows `DisplayName`; ids
  disambiguate, dup names are allowed.

## 12. Non-goals (YAGNI)

- No multi-select. No "me vs them" diff view. No filtering to self. No changes to export/import,
  storage, or overlay surfacing.
