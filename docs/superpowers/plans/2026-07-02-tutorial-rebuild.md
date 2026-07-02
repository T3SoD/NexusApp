# Welcome Tour Rebuild Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the legacy 11-step welcome tour with the approved 14-step map-first tour (spec: `docs/superpowers/specs/2026-07-02-tutorial-rebuild-design.md`).

**Architecture:** The coach-mark engine (TourController + CoachMarkWindow + HighlightWindow) is unchanged. Work is three layers: (1) additive anchor plumbing so every new step has a real element to ring, (2) a wholesale rewrite of the `TutorialTarget` enum, the `Steps` array, and MainWindow's resolver, guarded by unit tests on the step data, (3) runtime verification.

**Tech Stack:** C# / WPF (.NET 8), xUnit (NexusApp.Tests), repo `C:\Users\z_mw_\Dev\NexusApp`, branch `feature/help-tutorial-refresh`.

## Global Constraints

- No emojis and no em-dashes anywhere (copy uses " - " separators). Verbatim from spec.
- Mouse verbs only in copy; the app has no keyboard shortcuts by design.
- Never mention MrKraken/Kraken; commit identity is T3SoD noreply.
- EVERY git commit requires Zach's explicit per-commit confirmation first (house rule). The commit steps below are gated on that yes.
- Build must stay at 0 warnings / 0 errors after every task: `dotnet build NexusApp -c Debug` from the repo root.
- Kill any running NexusApp before building (locked exe fails the build): `try { Get-Process -Name NexusApp -ErrorAction Stop | Stop-Process -Force } catch {}`

---

### Task 1: Anchor plumbing (additive, no behavior change)

**Files:**
- Modify: `NexusApp/Views/MainWindow.xaml` (~lines 90, 101: the two telemetry chip Borders)
- Modify: `NexusApp/Views/CommandPage.cs` (KPI grid builder, the method containing `var grid = new Grid { Margin = new Thickness(0, 0, 0, 16), Height = 132 }`)
- Modify: `NexusApp/Views/OverlayWindow.xaml.cs` (~lines 29-45, the Welcome-tour targets block)
- Modify: `NexusApp/Views/MainWindow.xaml.cs` (~line 146, `PrepareOverlayForTutorial`)

**Interfaces:**
- Consumes: existing `SwitchTab(string)` on OverlayWindow (keys: "stats" = HUB, "scan", "hauling"); existing `SetRegionBtn`, `SetContractRegionBtn`, `HubScanBar` elements.
- Produces (used by Task 2's resolver): `MainWindow.SessionChip` and `MainWindow.BlueprintChip` (Border x:Name), `CommandPage.KpiRowTarget` (FrameworkElement?), `OverlayWindow.ContractRegionTarget` (FrameworkElement), `OverlayWindow.ShowHaulingTabForTutorial()`, `MainWindow.PrepareOverlayForTutorial(string tab = "scan")`.

- [ ] **Step 1: Name the two header pills in MainWindow.xaml**

The SESSION chip Border (comment "SESSION telemetry chip") becomes:

```xml
                <Border x:Name="SessionChip" Padding="9,3,11,3" CornerRadius="4" Margin="0,0,6,0"
                        Background="{StaticResource Bg2NavBrush}"
                        BorderBrush="{StaticResource NavBorderBrush}" BorderThickness="1"
                        ToolTip="Star Citizen session tracking (always on)">
```

The BLUEPRINTS chip Border (comment "BLUEPRINTS telemetry chip") becomes:

```xml
                <Border x:Name="BlueprintChip" Padding="9,3,11,3" CornerRadius="4" Margin="0,0,6,0"
                        Background="{StaticResource Bg2NavBrush}"
                        BorderBrush="{StaticResource NavBorderBrush}" BorderThickness="1"
                        ToolTip="Auto-Track Blueprints: collects blueprints you unlock to your library (always on)">
```

Only the `x:Name` attribute is added; everything else stays byte-identical.

- [ ] **Step 2: Expose the KPI row on CommandPage**

In `NexusApp/Views/CommandPage.cs`, add a field + property near the top of the class:

```csharp
    private Grid? _kpiRow;

    /// <summary>The KPI card row, for the welcome tour's Operations step to ring.</summary>
    public FrameworkElement? KpiRowTarget => _kpiRow;
```

In the KPI builder method, change `return grid;` (the one following the 5-column card loop) to:

```csharp
        _kpiRow = grid;
        return grid;
```

- [ ] **Step 3: Add the HAULING tour target to OverlayWindow.xaml.cs**

In the "Welcome-tour targets" block, add after the `HubTarget` line:

```csharp
    public FrameworkElement ContractRegionTarget => SetContractRegionBtn;   // HAULING tab's set-region link
```

And after `ShowHubTabForTutorial()`, add:

```csharp
    /// <summary>Force the HAULING tab visible so the tour can point at the contract scan controls.</summary>
    public void ShowHaulingTabForTutorial() => SwitchTab("hauling");
```

- [ ] **Step 4: Generalize PrepareOverlayForTutorial to a tab key**

Replace the current `PrepareOverlayForTutorial(bool hub = false)` in `MainWindow.xaml.cs` with:

```csharp
    /// <summary>Ensures the overlay is open, visible, and on the requested tab for the tour.</summary>
    private OverlayWindow? PrepareOverlayForTutorial(string tab = "scan")
    {
        EnsureOverlay();
        if (_overlay == null) return null;
        if (!_overlay.IsVisible) _overlay.Show();
        switch (tab)
        {
            case "hub": _overlay.ShowHubTabForTutorial(); break;
            case "hauling": _overlay.ShowHaulingTabForTutorial(); break;
            default: _overlay.ShowScanTabForTutorial(); break;
        }
        _overlay.UpdateLayout();
        return _overlay;
    }
```

Update the one existing call site that passes `hub: true` (the `TutorialTarget.OverlayHub` resolver arm) to `PrepareOverlayForTutorial("hub")` so the build stays green; Task 2 rewrites that switch anyway.

- [ ] **Step 5: Build**

Run: `dotnet build C:\Users\z_mw_\Dev\NexusApp\NexusApp -c Debug`
Expected: Build succeeded, 0 Warning(s), 0 Error(s).

- [ ] **Step 6: Commit (gated)**

Ask Zach for explicit confirmation, then:

```bash
git add NexusApp/Views/MainWindow.xaml NexusApp/Views/MainWindow.xaml.cs NexusApp/Views/CommandPage.cs NexusApp/Views/OverlayWindow.xaml.cs
git commit -m "feat(tour): anchor plumbing for rebuilt welcome tour (pills, KPI row, hauling tab)"
```

---

### Task 2: Rewrite the tour (enum + steps + resolver) with step-data tests

**Files:**
- Create: `NexusApp.Tests/TourStepTests.cs`
- Modify: `NexusApp/Views/TourController.cs` (enum + Step record + Steps array)
- Modify: `NexusApp/Views/MainWindow.xaml.cs` (`ResolveTutorialTarget`, ~lines 120-136)

**Interfaces:**
- Consumes: everything Task 1 produced, plus existing `DockTiles`, `NavScan`, `NavWork`, `NavHauling`, `NavNetwork` (dock tile x:Names), `OverlayToggleBtn`, `Anchor(string page, FrameworkElement element)`, `SetActivePage(string)`, `_commandPage` field and `InitCommandPage()` behavior (SetActivePage("command") lazily creates the page).
- Produces: `TourController.Steps` and `TourController.Step` become `internal` (test access via existing `InternalsVisibleTo("NexusApp.Tests")` in NexusApp.csproj); new public enum `TutorialTarget { None, SessionPill, BlueprintsPill, AppDock, OperationsKpis, RsDecoderTile, RefineryTile, HaulingTile, NetworkTile, OpenOverlay, OverlayHub, ScanToggle, ContractRegion }`.

- [ ] **Step 1: Write the failing tests**

Create `NexusApp.Tests/TourStepTests.cs`:

```csharp
using System;
using System.Linq;
using NexusApp.Views;
using Xunit;

namespace NexusApp.Tests;

public class TourStepTests
{
    [Fact]
    public void Tour_has_exactly_14_steps()
        => Assert.Equal(14, TourController.Steps.Length);

    [Fact]
    public void First_and_last_steps_are_centered()
    {
        Assert.Equal(TutorialTarget.None, TourController.Steps[0].Target);
        Assert.Equal(TutorialTarget.None, TourController.Steps[^1].Target);
    }

    [Fact]
    public void Every_anchored_target_is_used_exactly_once()
    {
        var anchored = TourController.Steps.Select(s => s.Target).Where(t => t != TutorialTarget.None).ToList();
        var expected = Enum.GetValues<TutorialTarget>().Where(t => t != TutorialTarget.None);
        Assert.Equal(anchored.Count, anchored.Distinct().Count());
        Assert.True(expected.All(anchored.Contains), "an enum target has no step");
    }

    [Fact]
    public void Copy_has_no_em_dashes_and_no_emoji()
    {
        foreach (var s in TourController.Steps)
        {
            var text = s.Title + s.Caption;
            Assert.DoesNotContain('—', text);          // em-dash
            Assert.DoesNotContain(text, c => char.IsSurrogate(c));  // emoji live above the BMP
        }
    }

    [Fact]
    public void Captions_fit_the_bubble()
    {
        foreach (var s in TourController.Steps)
        {
            Assert.False(string.IsNullOrWhiteSpace(s.Title));
            Assert.InRange(s.Caption.Length, 40, 300);
        }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test C:\Users\z_mw_\Dev\NexusApp\NexusApp.Tests --filter TourStepTests`
Expected: compile FAILURE ("'TourController.Steps' is inaccessible due to its protection level" and unknown enum members) - that is the red state.

- [ ] **Step 3: Rewrite TourController's enum, record, and steps**

In `NexusApp/Views/TourController.cs`, replace the enum with:

```csharp
/// <summary>Which control a tour step explains. The host resolves each to a live element.</summary>
public enum TutorialTarget
{
    None,            // centered step, no anchor
    SessionPill,     // GAME SESSION header pill
    BlueprintsPill,  // BLUEPRINTS header pill
    AppDock,         // the whole Wrist-OS module dock
    OperationsKpis,  // KPI card row on the Operations page
    RsDecoderTile,   // dock tile (ring only, no navigation)
    RefineryTile,    // dock tile (ring only, no navigation)
    HaulingTile,     // dock tile (ring only, no navigation)
    NetworkTile,     // dock tile (ring only, no navigation)
    OpenOverlay,     // overlay toggle button in the header
    OverlayHub,      // overlay HUB tab status lights
    ScanToggle,      // overlay SCAN tab Auto-scan RS switch
    ContractRegion,  // overlay HAULING tab set-contract-region link
}
```

Change `private sealed record Step(...)` to `internal sealed record Step(TutorialTarget Target, string Title, string Caption);` and `private static readonly Step[] Steps` to `internal static readonly Step[] Steps`, with this exact content (copy is final, from the approved spec):

```csharp
    internal static readonly Step[] Steps =
    [
        new(TutorialTarget.None, "Already working",
            "Nexus went live the moment you launched it - your game feeds it everything through one log file, no setup required. This quick tour shows you the map: a status strip that reports, a module dock that works, and an overlay that flies with you."),
        new(TutorialTarget.SessionPill, "The status strip",
            "This pill lights up when Star Citizen writes to Game.log and Nexus reads along - read-only, nothing modified, EAC-safe. If it stays dark, point Settings at your Game.log path."),
        new(TutorialTarget.BlueprintsPill, "Blueprints count themselves",
            "Pick up a blueprint in the verse and this count ticks on its own - every find lands in your Blueprint Library with zero clicks. WHERE TO MINE there shows how to get the ones you are missing."),
        new(TutorialTarget.AppDock, "The module dock",
            "Eight modules, one click each. When you want to do something instead of glance at it, it lives on this dock - Settings included, down at the bottom."),
        new(TutorialTarget.OperationsKpis, "Operations preflight",
            "Nexus lands here on launch. Read the row - last scan, refinery queue, cargo in transit, session blueprints, network coverage - and every panel below links straight into its module."),
        new(TutorialTarget.RsDecoderTile, "RS Decoder",
            "Your first stop after scanning a rock: click in, type the RS value, and get a best-match ore breakdown. Or skip the typing - auto-scan can read it off your screen."),
        new(TutorialTarget.RefineryTile, "Refinery",
            "Click + Add work order inside and Nexus counts down every job. The pill on this tile shows how many orders are cooking."),
        new(TutorialTarget.HaulingTile, "Hauling tracks itself",
            "Accept a hauling contract in game and it appears here on its own - the pill shows your active hauls. Click in for consolidated collect and deliver stops across all of them."),
        new(TutorialTarget.NetworkTile, "Share without a server",
            "Network trades blueprint libraries by file - export yours, import a friend's, see who owns what together. Fully offline; nothing leaves your machine unless you hand it over."),
        new(TutorialTarget.OpenOverlay, "The third space",
            "Click here to launch the overlay - a compact panel that floats over Star Citizen so you never leave the game to use Nexus."),
        new(TutorialTarget.OverlayHub, "Proof of life",
            "The overlay opens on the HUB: green lights mean a feed is live right now - session, RS auto-scan, contracts. Blueprints collected, server, and shard read out below."),
        new(TutorialTarget.ScanToggle, "Auto-scan is opt-in",
            "Switch this on and Nexus reads rock signatures straight off your screen through the magenta box you draw once. It pauses on its own whenever you and the game are both in the background."),
        new(TutorialTarget.ContractRegion, "Contracts get their own box",
            "Contract scanning uses a separate region, set right here - yellow for contracts, magenta stays RS. The two never interfere."),
        new(TutorialTarget.None, "You have the map",
            "Strip reports, dock works, overlay flies with you - and the loop is track, scan, refine, haul, share. One thing left: click Set up auto-scan to draw your magenta RS box now, or replay this tour anytime from Help."),
    ];
```

Nothing else in TourController changes (Start/Go/Render/Finish and the "Set up auto-scan" relabel already do the right thing).

- [ ] **Step 4: Rewrite the resolver in MainWindow.xaml.cs**

Replace the `ResolveTutorialTarget` switch body with:

```csharp
    private FrameworkElement? ResolveTutorialTarget(TutorialTarget t) => t switch
    {
        TutorialTarget.SessionPill     => SessionChip,
        TutorialTarget.BlueprintsPill  => BlueprintChip,
        TutorialTarget.AppDock         => DockTiles,
        TutorialTarget.OperationsKpis  => OperationsKpiAnchor(),
        TutorialTarget.RsDecoderTile   => NavScan,
        TutorialTarget.RefineryTile    => NavWork,
        TutorialTarget.HaulingTile     => NavHauling,
        TutorialTarget.NetworkTile     => NavNetwork,
        TutorialTarget.OpenOverlay     => OverlayToggleBtn,
        TutorialTarget.OverlayHub      => PrepareOverlayForTutorial("hub")?.HubTarget,
        TutorialTarget.ScanToggle      => PrepareOverlayForTutorial("scan")?.ScanToggleTarget,
        TutorialTarget.ContractRegion  => PrepareOverlayForTutorial("hauling")?.ContractRegionTarget,
        _                              => null,
    };

    // The Operations step navigates to the dashboard first, then rings its KPI row.
    private FrameworkElement? OperationsKpiAnchor()
    {
        SetActivePage("command");   // lazily creates + refreshes the dashboard
        return _commandPage?.KpiRowTarget ?? _commandPage;
    }
```

Delete the now-unused `Anchor(string, FrameworkElement)` helper ONLY if nothing else references it (the old CargoHauling/BlueprintNetwork arms were its only users); otherwise leave it.

- [ ] **Step 5: Run the tests**

Run: `dotnet test C:\Users\z_mw_\Dev\NexusApp\NexusApp.Tests --filter TourStepTests`
Expected: 5 tests PASS. Then run the full suite: `dotnet test C:\Users\z_mw_\Dev\NexusApp\NexusApp.Tests`
Expected: all existing tests still green.

- [ ] **Step 6: Build the app**

Run: `dotnet build C:\Users\z_mw_\Dev\NexusApp\NexusApp -c Debug`
Expected: Build succeeded, 0 Warning(s), 0 Error(s).

- [ ] **Step 7: Commit (gated)**

Ask Zach for explicit confirmation, then:

```bash
git add NexusApp/Views/TourController.cs NexusApp/Views/MainWindow.xaml.cs NexusApp.Tests/TourStepTests.cs
git commit -m "feat(tour): rebuild welcome tour as 14-step map-first flow with step-data tests"
```

---

### Task 3: Runtime verification

**Files:** none (verification only; fixes loop back into Task 1/2 files).

- [ ] **Step 1: Launch for the replay path**

Run (background): kill NexusApp, `dotnet build`, `dotnet run --project NexusApp --no-build`. Zach opens Help > Replay Tutorial and walks all 14 steps; Claude screenshots representative steps (2, 5, 11, 13) via the window-capture helper and inspects ring placement.

Checklist per spec: every anchored step rings the right control; page/tab navigation settles before the ring lands; steps 6-9 do NOT navigate (rings sit on dock tiles); Back from step 6 behaves; Skip works; finale's "Set up auto-scan" opens the region selector.

- [ ] **Step 2: First-run path**

Close the app. Back up then delete `%APPDATA%\NexusApp\settings.json`. Launch: the tour must auto-show once (no theme picker - removed), and not re-show on the next launch. Restore the settings backup afterward so Zach's real config returns.

- [ ] **Step 3: Fix anything found, re-run Task 2's tests, then final gated commit if fixes were needed**
