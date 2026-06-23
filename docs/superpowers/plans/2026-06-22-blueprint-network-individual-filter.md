# Blueprint Network — Individual Person Filter Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a user scope the Blueprint Network to a single member (not themselves) via a "Person" dropdown in the scope bar, with all three tabs reflecting only that person.

**Architecture:** Extract the current inline "which members + self-on-top" logic from the three tab builders into one pure, unit-tested resolver (`NetworkScope`). Add a `_personFilter` field and a Person dropdown to `NetworkPage`; a non-null person overrides the group scope and excludes the local user. Each tab renders the resolver's `ScopeIds` / `IncludeSelf` / `FocusPersonId`.

**Tech Stack:** C# / .NET 8 (`net8.0-windows`), WPF (code-behind UI, no XAML for these views), xUnit.

## Global Constraints

- **Spec:** `docs/superpowers/specs/2026-06-22-blueprint-network-individual-filter-design.md`. Parent: `2026-06-22-blueprint-network-design.md`.
- **Build/test only via Windows interop:** projects target `net8.0-windows`; there is **no native WSL dotnet**. Use `dotnet.exe` (on PATH at `/mnt/c/Program Files/dotnet/dotnet.exe`, standing-authorized for build/test). Interop runs are slow; if a run hangs, fall back to Zach's `~/test_nexus.ps1` on Windows or CI.
- **Git is gated:** every `git commit` (and any push) requires Zach's explicit per-commit confirmation **before** running. The commit step in each task is gated on that yes — do not commit unprompted. Branch: `feature/blueprint-network`.
- **Identity/PII:** commit author must be `T3SoD <…noreply…>`; **no Claude attribution**; **no PII** (no real name/email/`z_mw_`/`znoland`/scmdb) in any file, message, or metadata.
- **No emojis / emoticons** anywhere (code, UI copy, commit messages).
- **No new keyboard shortcuts** — the app is mouse-driven.
- **Logging:** scope changes log via `InteractionLog.Nav(...)` to match the sibling group-switch action (this is the App-Log-Monitor requirement for the feature).
- **Style:** match existing `NetworkPage` helpers — `Br("…Brush")`, `Head` (font), `ActionButton`, `SectionHeader`, `CoverageCell`. No new dependencies.
- **Behavior precedence:** a non-null `_personFilter` overrides `_groupFilter` and excludes self; selecting a group/All chip clears `_personFilter`.

---

### Task 1: Pure scope resolver + unit tests

**Files:**
- Create: `NexusApp/Services/NetworkScope.cs`
- Test: `NexusApp.Tests/NetworkScopeTests.cs`

**Interfaces:**
- Produces: `NexusApp.Services.NetworkScope.Resolve(string? personFilter, IReadOnlyList<string> groupOrAllMemberIds) -> NetworkScopeResult`
- Produces: `NetworkScopeResult { IReadOnlyList<string> ScopeIds; bool IncludeSelf; string? FocusPersonId; int CoverageDenominator; }` where `CoverageDenominator = ScopeIds.Count + (IncludeSelf ? 1 : 0)`.

- [ ] **Step 1: Write the failing tests**

Create `NexusApp.Tests/NetworkScopeTests.cs`:

```csharp
using System.Collections.Generic;
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

public class NetworkScopeTests
{
    private static readonly IReadOnlyList<string> GroupOrAll = new List<string> { "m1", "m2", "m3" };

    [Fact]
    public void PersonSelected_ScopesToThatPersonOnly_ExcludesSelf()
    {
        var r = NetworkScope.Resolve("m2", GroupOrAll);
        Assert.Equal(new[] { "m2" }, r.ScopeIds);
        Assert.False(r.IncludeSelf);
        Assert.Equal("m2", r.FocusPersonId);
        Assert.Equal(1, r.CoverageDenominator);
    }

    [Fact]
    public void NoPerson_UsesGivenMembers_IncludesSelf()
    {
        var r = NetworkScope.Resolve(null, GroupOrAll);
        Assert.Equal(GroupOrAll, r.ScopeIds);
        Assert.True(r.IncludeSelf);
        Assert.Null(r.FocusPersonId);
        Assert.Equal(4, r.CoverageDenominator); // 3 members + self
    }

    [Fact]
    public void PersonFilter_OverridesTheMemberList()
    {
        var r = NetworkScope.Resolve("m1", GroupOrAll);
        Assert.Equal(new[] { "m1" }, r.ScopeIds);   // the group/all list is ignored
        Assert.False(r.IncludeSelf);
        Assert.Equal("m1", r.FocusPersonId);
    }

    [Fact]
    public void EmptyPersonString_TreatedAsNoPerson()
    {
        var r = NetworkScope.Resolve("", GroupOrAll);
        Assert.Null(r.FocusPersonId);
        Assert.True(r.IncludeSelf);
        Assert.Equal(GroupOrAll, r.ScopeIds);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet.exe test NexusApp.Tests/NexusApp.Tests.csproj --filter "FullyQualifiedName~NetworkScopeTests"`
Expected: FAIL to compile — `NetworkScope` / `NetworkScopeResult` do not exist.

- [ ] **Step 3: Write the minimal implementation**

Create `NexusApp/Services/NetworkScope.cs`:

```csharp
using System.Collections.Generic;

namespace NexusApp.Services;

/// <summary>
/// Pure resolution of which members a Blueprint Network view should cover. A non-null
/// <paramref name="personFilter"/> focuses a single member and excludes the local user; otherwise the
/// supplied group-or-all set is used and the local user is counted on top. No UI/storage deps.
/// </summary>
public static class NetworkScope
{
    public static NetworkScopeResult Resolve(string? personFilter, IReadOnlyList<string> groupOrAllMemberIds)
    {
        if (!string.IsNullOrEmpty(personFilter))
            return new NetworkScopeResult(new[] { personFilter }, includeSelf: false, focusPersonId: personFilter);

        return new NetworkScopeResult(groupOrAllMemberIds, includeSelf: true, focusPersonId: null);
    }
}

public sealed class NetworkScopeResult
{
    public NetworkScopeResult(IReadOnlyList<string> scopeIds, bool includeSelf, string? focusPersonId)
    {
        ScopeIds = scopeIds;
        IncludeSelf = includeSelf;
        FocusPersonId = focusPersonId;
    }

    public IReadOnlyList<string> ScopeIds { get; }
    public bool IncludeSelf { get; }
    public string? FocusPersonId { get; }
    public int CoverageDenominator => ScopeIds.Count + (IncludeSelf ? 1 : 0);
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet.exe test NexusApp.Tests/NexusApp.Tests.csproj --filter "FullyQualifiedName~NetworkScopeTests"`
Expected: PASS (4 passed).

- [ ] **Step 5: Commit** *(gated — confirm with Zach first)*

```bash
git add NexusApp/Services/NetworkScope.cs NexusApp.Tests/NetworkScopeTests.cs
git commit -m "Blueprint Network: add pure scope resolver + tests"
```

---

### Task 2: Route the three views through the resolver (self-aware)

Behavior-preserving while `_personFilter` is null (resolver returns `IncludeSelf = true` and the same member set). Wires `IncludeSelf`/`FocusPersonId` so later tasks can flip behavior by setting `_personFilter`.

**Files:**
- Modify: `NexusApp/Views/NetworkPage.cs` (field ~24; `BuildMembersView` ~198-205; `BuildBlueprintsView` ~318-355; `BuildOverviewView` ~444-484; add `PersonName` helper)

**Interfaces:**
- Consumes: `NetworkScope.Resolve(...)`, `NetworkScopeResult` (Task 1).
- Produces: `private string? _personFilter` and `private string PersonName(string id)` used by Tasks 3-5.

- [ ] **Step 1: Add the `_personFilter` field**

In `NexusApp/Views/NetworkPage.cs`, just below the existing `_groupFilter` field (line 24):

```csharp
    private string? _groupFilter;        // null = All, else a group id
    private string? _personFilter;       // null = none, else a member id (overrides _groupFilter, excludes self)
```

- [ ] **Step 2: Add the `PersonName` helper**

Add near the other small private helpers (e.g., next to `SectionLabel`, ~line 836):

```csharp
    private string PersonName(string id) => _store.GetMember(id)?.DisplayName ?? "this member";
```

- [ ] **Step 3: Refactor `BuildMembersView` scope (lines 200-205)**

Replace:

```csharp
        var members = _store.GetMembers();
        if (_groupFilter != null)
        {
            var ids = new HashSet<string>(_store.GetGroupMemberIds(_groupFilter));
            members = members.Where(m => ids.Contains(m.Id)).ToList();
        }
```

with:

```csharp
        var groupOrAll = _groupFilter != null
            ? _store.GetGroupMemberIds(_groupFilter)
            : _store.GetMembers().Select(m => m.Id).ToList();
        var scope = NetworkScope.Resolve(_personFilter, groupOrAll);
        var scopeSet = new HashSet<string>(scope.ScopeIds);
        var members = _store.GetMembers().Where(m => scopeSet.Contains(m.Id)).ToList();
```

- [ ] **Step 4: Refactor `BuildBlueprintsView` scope + note (lines 318-355)**

Replace the scope/`total`/`counts`/`Owned` block (318-326):

```csharp
        // Member scope: the selected group, or everyone. Self is always counted on top.
        var scopeIds = _groupFilter != null
            ? _store.GetGroupMemberIds(_groupFilter)
            : _store.GetMembers().Select(m => m.Id).ToList();
        var total = scopeIds.Count + 1;
        var counts = _store.OwnerCounts(scopeIds);

        int Owned(string name) =>
            (counts.TryGetValue(name, out var c) ? c : 0) + (_settings.IsBlueprintOwned(name) ? 1 : 0);
```

with:

```csharp
        // Member scope: a focused person, the selected group, or everyone. The local user is counted
        // on top unless a single person is in focus (see NetworkScope).
        var groupOrAll = _groupFilter != null
            ? _store.GetGroupMemberIds(_groupFilter)
            : _store.GetMembers().Select(m => m.Id).ToList();
        var scope = NetworkScope.Resolve(_personFilter, groupOrAll);
        var scopeIds = scope.ScopeIds;
        var total = scope.CoverageDenominator;
        var counts = _store.OwnerCounts(scopeIds);

        int Owned(string name) =>
            (counts.TryGetValue(name, out var c) ? c : 0)
            + (scope.IncludeSelf && _settings.IsBlueprintOwned(name) ? 1 : 0);
```

Then replace the coverage note (350-354):

```csharp
        var note = new TextBlock
        {
            Text = $"Coverage across you + {scopeIds.Count} member{(scopeIds.Count == 1 ? "" : "s")}"
                 + (_groupFilter != null ? " in this group." : "."),
            Foreground = Br("FgDimBrush"), FontSize = 11, Margin = new Thickness(2, 0, 0, 8),
        };
```

with:

```csharp
        var note = new TextBlock
        {
            Text = scope.FocusPersonId != null
                ? $"Showing {PersonName(scope.FocusPersonId)}'s blueprints."
                : $"Coverage across you + {scopeIds.Count} member{(scopeIds.Count == 1 ? "" : "s")}"
                  + (_groupFilter != null ? " in this group." : "."),
            Foreground = Br("FgDimBrush"), FontSize = 11, Margin = new Thickness(2, 0, 0, 8),
        };
```

- [ ] **Step 5: Refactor `BuildOverviewView` scope + member cards (lines 444-484)**

Replace the scope/`Owned` block (444-449):

```csharp
        var scopeIds = _groupFilter != null
            ? _store.GetGroupMemberIds(_groupFilter)
            : _store.GetMembers().Select(m => m.Id).ToList();
        var scopeSet = new HashSet<string>(scopeIds);
        var counts = _store.OwnerCounts(scopeIds);
        int Owned(string n) => (counts.TryGetValue(n, out var c) ? c : 0) + (_settings.IsBlueprintOwned(n) ? 1 : 0);
```

with:

```csharp
        var groupOrAll = _groupFilter != null
            ? _store.GetGroupMemberIds(_groupFilter)
            : _store.GetMembers().Select(m => m.Id).ToList();
        var scope = NetworkScope.Resolve(_personFilter, groupOrAll);
        var scopeIds = scope.ScopeIds;
        var scopeSet = new HashSet<string>(scopeIds);
        var counts = _store.OwnerCounts(scopeIds);
        int Owned(string n) => (counts.TryGetValue(n, out var c) ? c : 0)
            + (scope.IncludeSelf && _settings.IsBlueprintOwned(n) ? 1 : 0);
```

Then replace the member-cards seed + loop (471-480):

```csharp
        var people = new List<(string name, int count, bool self)>
        {
            (string.IsNullOrEmpty(_settings.Current.LocalDisplayName) ? "You" : $"{_settings.Current.LocalDisplayName} (you)",
                _settings.OwnedBlueprintCount, true),
        };
        foreach (var m in _store.GetMembers())
        {
            if (_groupFilter != null && !scopeSet.Contains(m.Id)) continue;
            people.Add((m.DisplayName, ownedCounts.TryGetValue(m.Id, out var c) ? c : 0, false));
        }
```

with:

```csharp
        var people = new List<(string name, int count, bool self)>();
        if (scope.IncludeSelf)
            people.Add((string.IsNullOrEmpty(_settings.Current.LocalDisplayName) ? "You" : $"{_settings.Current.LocalDisplayName} (you)",
                _settings.OwnedBlueprintCount, true));
        foreach (var m in _store.GetMembers())
        {
            if (!scopeSet.Contains(m.Id)) continue;
            people.Add((m.DisplayName, ownedCounts.TryGetValue(m.Id, out var c) ? c : 0, false));
        }
```

- [ ] **Step 6: Build to verify no behavior change**

Run: `dotnet.exe test NexusApp.Tests/NexusApp.Tests.csproj --filter "FullyQualifiedName~NetworkScopeTests"`
Expected: PASS (compiles the app + tests; resolver tests still green). `_personFilter` is null everywhere, so the Network tabs render identically to before.

- [ ] **Step 7: Commit** *(gated — confirm with Zach first)*

```bash
git add NexusApp/Views/NetworkPage.cs
git commit -m "Blueprint Network: route view scope through resolver (self-aware)"
```

---

### Task 3: Person dropdown + state wiring + logging

Adds the control that sets `_personFilter`. After this, picking a person scopes all tabs to them (data correct); clearing returns to group/All. Blueprints filter chips and the Overview callout are refined in Tasks 4-5.

**Files:**
- Modify: `NexusApp/Views/NetworkPage.cs` (usings; scope-bar layout ~96-97; ctor ~102-103; `GroupChip` click ~165-172; add `_personPickerHost` field, `RenderPersonPicker`, `ShowPersonMenu`, `SetPersonFilter`; guard in `RenderContent` ~187)

**Interfaces:**
- Consumes: `_personFilter`, `PersonName` (Task 2).
- Produces: `SetPersonFilter(string?)`, `RenderPersonPicker()`.

- [ ] **Step 1: Ensure the Popup using is present**

At the top of `NexusApp/Views/NetworkPage.cs`, confirm/add:

```csharp
using System.Windows.Controls.Primitives;  // Popup, PlacementMode
```

- [ ] **Step 2: Add the picker host field**

Below `private readonly WrapPanel _groupBar = new();` (line 27):

```csharp
    private readonly Border _personPickerHost = new();   // sits at the right of the scope bar
```

- [ ] **Step 3: Put chips + picker on one row (replace lines 96-97)**

Replace:

```csharp
        _groupBar.Margin = new Thickness(0, 0, 0, 12);
        Grid.SetRow(_groupBar, 2); root.Children.Add(_groupBar);
```

with:

```csharp
        var scopeRow = new DockPanel { Margin = new Thickness(0, 0, 0, 12), LastChildFill = true };
        _personPickerHost.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(_personPickerHost, Dock.Right);
        scopeRow.Children.Add(_personPickerHost);   // right
        _groupBar.Margin = new Thickness(0);
        scopeRow.Children.Add(_groupBar);           // fills the rest (left)
        Grid.SetRow(scopeRow, 2); root.Children.Add(scopeRow);
```

- [ ] **Step 4: Render the picker on load (replace lines 102-103)**

Replace:

```csharp
        RenderGroups();
        RenderContent();
```

with:

```csharp
        RenderGroups();
        RenderPersonPicker();
        RenderContent();
```

- [ ] **Step 5: Clear the person focus when a group/All chip is clicked (replace lines 167-172)**

Replace:

```csharp
            if (isNew) { CreateGroupPrompt(); return; }
            _groupFilter = groupId;
            InteractionLog.Nav("Blueprint Network: group filter");
            RenderGroups();
            RenderContent();
```

with:

```csharp
            if (isNew) { CreateGroupPrompt(); return; }
            _groupFilter = groupId;
            _personFilter = null;   // selecting a group/All clears the person focus
            InteractionLog.Nav("Blueprint Network: group filter");
            RenderGroups();
            RenderPersonPicker();
            RenderContent();
```

- [ ] **Step 6: Add the picker + menu + state methods**

Add after `RenderGroups()` / `GroupChip` (after line 174):

```csharp
    // ── person picker ─────────────────────────────────────────────────────────────

    private void RenderPersonPicker()
    {
        if (_store.MemberCount == 0)   // nothing to filter to
        {
            _personPickerHost.Child = null;
            _personPickerHost.Visibility = Visibility.Collapsed;
            return;
        }
        _personPickerHost.Visibility = Visibility.Visible;

        var label = _personFilter == null ? "All" : PersonName(_personFilter);
        var tb = new TextBlock
        {
            Text = $"Person: {label}  ▾", FontFamily = Head, FontSize = 11, FontWeight = FontWeights.Bold,
            Foreground = _personFilter == null ? Br("FgDimBrush") : Br("AccentBrush"),
        };
        var btn = new Border
        {
            Padding = new Thickness(11, 6, 11, 6), CornerRadius = new CornerRadius(13),
            Background = Br("Bg2NavBrush"), BorderBrush = Br("NavBorderBrush"), BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand, Child = tb,
        };
        btn.ToolTip = "Show one person's blueprints (you are excluded)";
        btn.MouseLeftButtonUp += (_, _) => ShowPersonMenu(btn);
        _personPickerHost.Child = btn;
    }

    private void ShowPersonMenu(UIElement anchor)
    {
        var list = new StackPanel();
        var popup = new Popup
        {
            PlacementTarget = anchor, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true,
        };
        void AddRow(string text, string? id)
        {
            var rtb = new TextBlock { Text = text, FontSize = 12, Foreground = Br("FgBrush"), Margin = new Thickness(12, 7, 16, 7) };
            var row = new Border { Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = rtb };
            row.MouseLeftButtonUp += (_, _) => { popup.IsOpen = false; SetPersonFilter(id); };
            list.Children.Add(row);
        }
        AddRow("All", null);
        foreach (var m in _store.GetMembers()) AddRow(m.DisplayName, m.Id);

        popup.Child = new Border
        {
            Background = Br("BgBrush"), BorderBrush = Br("NavBorderBrush"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Child = new ScrollViewer { MaxHeight = 280, Content = list },
        };
        popup.IsOpen = true;
    }

    private void SetPersonFilter(string? id)
    {
        _personFilter = string.IsNullOrEmpty(id) ? null : id;
        InteractionLog.Nav(_personFilter == null
            ? "Blueprint Network: person filter cleared"
            : "Blueprint Network: person filter");
        RenderGroups();
        RenderPersonPicker();
        RenderContent();
    }
```

- [ ] **Step 7: Guard against a removed focused person (in `RenderContent`, line 187)**

Replace:

```csharp
    private void RenderContent()
    {
        _host.Child = _tab switch
```

with:

```csharp
    private void RenderContent()
    {
        if (_personFilter != null && _store.GetMember(_personFilter) == null)
            _personFilter = null;   // focused member was removed or absent after a re-import
        _host.Child = _tab switch
```

- [ ] **Step 8: Build + manual smoke**

Run: `dotnet.exe test NexusApp.Tests/NexusApp.Tests.csproj --filter "FullyQualifiedName~NetworkScopeTests"`
Expected: PASS (compiles).
Manual (Zach, `~/test_nexus.ps1`): import 1-2 `.nexuslib` files → a "Person: All" chip appears at the right of the scope bar → pick a person → all three tabs show only them (Members shows one row; Blueprints/Overview counts reflect just them, you excluded) → "All" or a group chip clears it.

- [ ] **Step 9: Commit** *(gated — confirm with Zach first)*

```bash
git add NexusApp/Views/NetworkPage.cs
git commit -m "Blueprint Network: add Person filter dropdown"
```

---

### Task 4: Person-relative filters + cell on the Blueprints tab

When a person is focused, the group filters ("Nobody owns / Single owner / I'm missing") don't apply. Swap to person-relative chips (All / Owns / Missing) and an owned/missing cell.

**Files:**
- Modify: `NexusApp/Views/NetworkPage.cs` (`BuildBlueprintsView` filters + list construction ~335-347; add `PersonOwnCell` helper)

**Interfaces:**
- Consumes: `scope.FocusPersonId`, `Owned(...)`, `total` (Task 2).

- [ ] **Step 1: Branch the filters + cell (replace lines 335-347)**

Replace:

```csharp
        var filters = new List<BlueprintListView.FilterChip>
        {
            new() { Label = "All",          Match = _ => true },
            new() { Label = "Nobody owns",  Match = b => Owned(b.Name) == 0 },
            new() { Label = "Single owner", Match = b => Owned(b.Name) == 1 },
            new() { Label = "I'm missing",  Match = b => !_settings.IsBlueprintOwned(b.Name) },
        };

        var listView = new BlueprintListView(
            all,
            b => CoverageCell(Owned(b.Name), total),
            b => OwnersPanel(b.Name, scopeIds),
            filters);
```

with:

```csharp
        List<BlueprintListView.FilterChip> filters;
        Func<Blueprint, UIElement> cell;
        if (scope.FocusPersonId != null)
        {
            filters = new List<BlueprintListView.FilterChip>
            {
                new() { Label = "All",     Match = _ => true },
                new() { Label = "Owns",    Match = b => Owned(b.Name) >= 1 },
                new() { Label = "Missing", Match = b => Owned(b.Name) == 0 },
            };
            cell = b => PersonOwnCell(Owned(b.Name) >= 1);
        }
        else
        {
            filters = new List<BlueprintListView.FilterChip>
            {
                new() { Label = "All",          Match = _ => true },
                new() { Label = "Nobody owns",  Match = b => Owned(b.Name) == 0 },
                new() { Label = "Single owner", Match = b => Owned(b.Name) == 1 },
                new() { Label = "I'm missing",  Match = b => !_settings.IsBlueprintOwned(b.Name) },
            };
            cell = b => CoverageCell(Owned(b.Name), total);
        }

        var listView = new BlueprintListView(
            all,
            cell,
            b => OwnersPanel(b.Name, scopeIds),
            filters);
```

- [ ] **Step 2: Add the `PersonOwnCell` helper**

Add near `CoverageCell` (search for `private UIElement CoverageCell`):

```csharp
    private UIElement PersonOwnCell(bool owns) => new TextBlock
    {
        Text = owns ? "owned" : "missing",
        Foreground = owns ? Br("AccentBrush") : Br("FgDimBrush"),
        FontSize = 11.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
    };
```

- [ ] **Step 3: Build + manual smoke**

Run: `dotnet.exe test NexusApp.Tests/NexusApp.Tests.csproj --filter "FullyQualifiedName~NetworkScopeTests"`
Expected: PASS (compiles).
Manual: focus a person → Blueprints chips read **All / Owns / Missing**; rows show **owned** / **missing**; clearing restores the group chips (All / Nobody owns / Single owner / I'm missing).

- [ ] **Step 4: Commit** *(gated — confirm with Zach first)*

```bash
git add NexusApp/Views/NetworkPage.cs
git commit -m "Blueprint Network: person-relative filters on Blueprints tab"
```

---

### Task 5: Person-focused Overview (suppress single-owner callout)

When a person is focused, the "single owner" watch list is meaningless. Suppress it and show that person's missing summary instead.

**Files:**
- Modify: `NexusApp/Views/NetworkPage.cs` (`BuildOverviewView` watch-list section ~486-505)

**Interfaces:**
- Consumes: `scope.FocusPersonId`, `PersonName`, `nobody`, `total`, `single`, `singleBps` (Task 2 + existing).

- [ ] **Step 1: Branch the watch-list section (replace lines 486-505)**

Replace:

```csharp
        // Watch list: gaps + single-owner risk.
        body.Children.Add(SectionHeader("Watch list"));
        body.Children.Add(WatchSummary(nobody, single));
        if (singleBps.Count > 0)
        {
            var nameMap = _store.GetMembers().ToDictionary(m => m.Id, m => m.DisplayName);
            string OwnerOf(string bp)
            {
                if (_settings.IsBlueprintOwned(bp)) return "you";
                var id = _store.OwnerIdsOf(bp).FirstOrDefault(scopeSet.Contains);
                return id != null && nameMap.TryGetValue(id, out var nm) ? nm : "?";
            }
            foreach (var bp in singleBps) body.Children.Add(SingleOwnerRow(bp, OwnerOf(bp)));
            if (single > singleBps.Count)
                body.Children.Add(new TextBlock
                {
                    Text = $"+{single - singleBps.Count} more single-owner blueprints", Foreground = Br("FgDimBrush"),
                    FontSize = 11.5, Margin = new Thickness(4, 6, 0, 0),
                });
        }
```

with:

```csharp
        if (scope.FocusPersonId != null)
        {
            // One-person scope: the single-owner callout is meaningless; show their gap instead.
            body.Children.Add(SectionHeader("Missing"));
            body.Children.Add(new TextBlock
            {
                Text = $"{PersonName(scope.FocusPersonId)} is missing {nobody} of {total} blueprint{(total == 1 ? "" : "s")}.",
                Foreground = Br("FgDimBrush"), FontSize = 12.5, Margin = new Thickness(4, 4, 0, 0), TextWrapping = TextWrapping.Wrap,
            });
        }
        else
        {
            // Watch list: gaps + single-owner risk.
            body.Children.Add(SectionHeader("Watch list"));
            body.Children.Add(WatchSummary(nobody, single));
            if (singleBps.Count > 0)
            {
                var nameMap = _store.GetMembers().ToDictionary(m => m.Id, m => m.DisplayName);
                string OwnerOf(string bp)
                {
                    if (_settings.IsBlueprintOwned(bp)) return "you";
                    var id = _store.OwnerIdsOf(bp).FirstOrDefault(scopeSet.Contains);
                    return id != null && nameMap.TryGetValue(id, out var nm) ? nm : "?";
                }
                foreach (var bp in singleBps) body.Children.Add(SingleOwnerRow(bp, OwnerOf(bp)));
                if (single > singleBps.Count)
                    body.Children.Add(new TextBlock
                    {
                        Text = $"+{single - singleBps.Count} more single-owner blueprints", Foreground = Br("FgDimBrush"),
                        FontSize = 11.5, Margin = new Thickness(4, 6, 0, 0),
                    });
            }
        }
```

- [ ] **Step 2: Build + manual smoke**

Run: `dotnet.exe test NexusApp.Tests/NexusApp.Tests.csproj --filter "FullyQualifiedName~NetworkScopeTests"`
Expected: PASS (compiles).
Manual: focus a person → Overview shows their coverage band + their single card + a "**… is missing N of M blueprints**" line, and **no single-owner rows**; clearing restores the full Watch list.

- [ ] **Step 3: Commit** *(gated — confirm with Zach first)*

```bash
git add NexusApp/Views/NetworkPage.cs
git commit -m "Blueprint Network: person-focused Overview (suppress single-owner callout)"
```

---

## Self-Review

**1. Spec coverage** — every spec section maps to a task:
- §3 entry point (Person dropdown) → Task 3. §3 breadth (all tabs) → Tasks 2-5. §3 self excluded → Task 1 (`IncludeSelf`) + Task 2 (wiring). §3 Overview suppression → Task 5.
- §4 state/precedence (`_personFilter` overrides) → Task 1 (resolver) + Task 3 (clear-on-chip).
- §5 testable resolver → Task 1.
- §6 dropdown (All + members, clear, empty-state hidden) → Task 3 (`RenderPersonPicker` collapses when `MemberCount == 0`).
- §7 per-tab behavior → Members (Task 2), Blueprints (Tasks 2+4), Overview (Tasks 2+5).
- §8 logging → Task 3 (`InteractionLog.Nav`, matching the group switch).
- §9 tests → Task 1.
- §11 edge cases: removed person → Task 3 Step 7 guard; group precedence → Task 3 Step 5; dup names → keyed by id throughout (`SetPersonFilter(m.Id)`, `_store.GetMember(id)`).

**2. Placeholder scan** — none; every code step shows complete code, every run step shows the command + expected result.

**3. Type consistency** — `NetworkScope.Resolve(string?, IReadOnlyList<string>)` and `NetworkScopeResult` (`ScopeIds`/`IncludeSelf`/`FocusPersonId`/`CoverageDenominator`) are used identically in Tasks 2-5. `_personFilter` (string?), `PersonName(string)`, `SetPersonFilter(string?)`, `RenderPersonPicker()`, `PersonOwnCell(bool)` are defined once and referenced consistently.

Note: the resolver signature is the cleaner two-arg form (the spec's three-arg shape was illustrative; group→member-id resolution stays at the call site, exactly as the code does today). Logging uses `InteractionLog.Nav` to match the sibling group action rather than the spec's illustrative `[NET]` string.
