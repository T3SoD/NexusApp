# Cargo Hauling Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a fully automatic, Game.log-driven cargo-hauling tracker to NexusApp: it shows active and finished hauls per mission and a "where to load / where to drop" consolidation view, in both the main window and the overlay.

**Architecture:** A standalone `HaulTracker` owns its own `GameLogWatcher` (mirroring `GameLogSession`) and parses haul events out of the tailed Star Citizen Game.log via a pure static `HaulLogParser`. The parser + tracker + consolidation logic are headless-testable through a public `Ingest(GameLogEntry)` method (exactly like `GameLogSession`). The UI (main-window domain tab + overlay glance tab) binds to the tracker's events. No second file is read beyond Game.log; everything is auto-derived. No manual entry, no payout, no persistence in v1 (state is rebuilt by replaying the current Game.log on startup).

**Tech Stack:** C# / .NET 8 (net8.0-windows), WPF, xUnit 2.9.2. Reuses `GameLogWatcher`, `Logger`, MVVM patterns already in the repo.

## Global Constraints

- **Branch:** all work on `feature/cargo-hauling` (already created off `main @ 61c837d`). Never commit/push/merge/release without Zach's explicit per-action "yes". A "yes" is per-commit and does not carry forward.
- **No emojis** anywhere (code, comments, UI, commits, docs).
- **No em-dashes** anywhere; use periods, commas, parentheses, or hyphens.
- **No Claude attribution** in commits or PRs.
- **No PII ever:** the `EndMission` line and marker DataBank lines contain `Player[<handle>]` and `PlayerId[<GEID>]` (observed handle: the user's old RSI handle). The parser must never extract, store, log, or surface either. Parse only `MissionId` and `CompletionType` from `EndMission`.
- **Definition of done includes logging:** wire a new `[HAUL]` tag to nexus.log via `Logger.Info(...)` so the feature is visible in the App Log Monitor + diagnostic snapshot. (Freeform string tag; no enum needed, matches `[NET]`/`[SCAN]` convention.)
- **OcrService / auto-scan preprocessing is locked** and unrelated; do not touch it.
- **WPF cannot be built in WSL.** The testable core (Tasks 1-4) is verified with `dotnet test`. UI tasks (5-8) compile-verify via CI build-check on push and runtime-verify by Zach on Windows (`~/test_nexus.ps1`).

## Game.log event contract (verified against current build 12061511, 2026-06-27)

Recon source of truth: `scratchpad/cargo_haul_gamelog_recon.md`. Real (redacted) lines are embedded as test fixtures below. The five line shapes the tracker consumes:

1. **Marker** (one per objective; re-emitted on zone reload, must dedupe by objectiveId):
   `<...> <CLocalMissionPhaseMarker::CreateMarker> Creating objective marker: missionId [GUID], generator name [Covalex_Hauling], contract [HaulCargo_AToB_...], contractDefinitionId[...], objectiveId [pickup_<g>_<leg> | dropoff_<g>_<leg>], markerEntityId [N], zoneHostId [N], position [x: .., y: .., z: ..] [Team_MissionFeatures][Missions]`
   - Haul-specific because `contract` contains `HaulCargo` or `CargoHauling`.
2. **Deliver objective** (DROPOFF only; gives commodity + target SCU + destination):
   `<...> <SHUDEvent_OnNotification> Added notification "New Objective: Deliver 0/<M> SCU of <commodity> to <destination>: " [n] to queue. New queue size: N, MissionId: [GUID], ObjectiveId: [dropoff_<g>_<leg>] [...]`
   - Haul-specific because of `Deliver <n>/<n> SCU of`. (Bounty "Deliver data" objectives have no `SCU`.)
   - NOTE: the current count is always `0`; live progress is NOT logged. Only the target `M` matters.
3. **Contract Accepted** (route title; `<EMx>` markup must be stripped):
   `<...> Added notification "Contract Accepted:  <Title>: " [n] to queue. ... MissionId: [GUID], ObjectiveId: [] [...]`
4. **Objective completed** (binary per-leg progress):
   `<...> <ObjectiveUpserted> Received ObjectiveUpserted push message for: mission_id GUID - objective_id <pickup|dropoff>_<g>_<leg> - state MISSION_OBJECTIVE_STATE_COMPLETED - created 0 - flags=... [...]`
5. **EndMission** (final outcome):
   `<...> <EndMission> Ending mission for player. MissionId[GUID] Player[..] PlayerId[..] CompletionType[<Complete|Abandon|Fail|Deactivate>] Reason[..] [...]`

Lines 4 and 5 are generic to ALL missions, so the tracker applies them only to mission IDs it already knows are hauls (established by line 1 or line 2).

## File Structure

**New (testable core, Tasks 1-4):**
- `NexusApp/Models/Haul.cs` - `Haul`, `HaulLeg`, `HaulRole`, `HaulOutcome`, `Consolidation`, `ConsolidationStop`.
- `NexusApp/Services/HaulLogParser.cs` - pure static line parser + helpers, returns small record types.
- `NexusApp/Services/HaulTracker.cs` - stateful aggregator; owns a `GameLogWatcher`; public `Ingest(GameLogEntry)`, `Reset()`, `BuildConsolidation()`; events.
- `NexusApp.Tests/HaulLogParserTests.cs`
- `NexusApp.Tests/HaulTrackerTests.cs`

**New (UI, Task 7):**
- `NexusApp/Views/HaulingPage.cs` - code-built `UserControl` for the main-window Hauling domain (mirrors `Views/NetworkPage.cs`).

**Modified (UI, Tasks 5-8):**
- `NexusApp/App.xaml.cs` - add `public static HaulTracker Hauls`, construct + start (replay) + dispose.
- `NexusApp/Views/MainWindow.xaml` + `.xaml.cs` - `NavHauling` RadioButton, `PageHauling` host, `SetActivePage` + `InitHaulingPage`.
- `NexusApp/Views/OverlayWindow.xaml` + `.xaml.cs` - `HAULING` tab button/indicator/content + `SwitchTab` + `RebuildHaulingPanel`.

---

### Task 1: Haul domain model

**Files:**
- Create: `NexusApp/Models/Haul.cs`
- Test: (covered indirectly by Tasks 2-4; no standalone test for plain data holders)

**Interfaces:**
- Produces: `NexusApp.Models.Haul`, `HaulLeg`, `enum HaulRole { Pickup, Dropoff }`, `enum HaulOutcome { Active, Complete, Abandoned, Failed, Deactivated }`, `Consolidation`, `ConsolidationStop`.

- [ ] **Step 1: Create the model file**

```csharp
namespace NexusApp.Models;

public enum HaulRole { Pickup, Dropoff }

// Mirrors EndMission CompletionType values, plus Active while the mission is open.
public enum HaulOutcome { Active, Complete, Abandoned, Failed, Deactivated }

// One pickup or dropoff objective of a haul. Pickup and dropoff that move the same
// cargo share CargoKey (the objective GUID base + leg index), so a pickup can borrow
// its sibling dropoff's commodity / SCU for display.
public sealed class HaulLeg
{
    public string ObjectiveId { get; init; } = "";   // e.g. "dropoff_6e9335e8-..._0"
    public string CargoKey { get; init; } = "";       // "<guidBase>_<leg>" shared by the pickup/dropoff pair
    public HaulRole Role { get; init; }
    public int LegIndex { get; init; }
    public string Commodity { get; set; } = "";       // dropoff only (from the Deliver line)
    public int TargetScu { get; set; }                // dropoff only
    public string Destination { get; set; } = "";     // dropoff only (the "to <X>")
    public double X { get; init; }
    public double Y { get; init; }
    public double Z { get; init; }
    public bool Completed { get; set; }
}

public sealed class Haul
{
    public string MissionId { get; init; } = "";
    public string Company { get; set; } = "";         // display name, e.g. "Red Wind"
    public string ContractName { get; set; } = "";    // raw contract token
    public string Topology { get; set; } = "Unknown"; // "1 to 1" / "1 to 2" / "1 to 3" / "2 to 1" / "Unknown"
    public string RouteTitle { get; set; } = "";      // Contract Accepted title, EM tags stripped
    public string PickupName { get; set; } = "";      // best-effort, left side of an "A > B" route title
    public HaulOutcome Outcome { get; set; } = HaulOutcome.Active;
    public DateTime StartedAt { get; init; } = DateTime.Now;
    public List<HaulLeg> Legs { get; } = new();

    public bool IsActive => Outcome == HaulOutcome.Active;
    public HaulLeg? LegByObjective(string objectiveId) =>
        Legs.Find(l => l.ObjectiveId == objectiveId);
}

// A single place to act on, with everything to load or drop there across all active hauls.
public sealed class ConsolidationStop
{
    public string Location { get; init; } = "";
    public List<(string Commodity, int Scu, string MissionId)> Items { get; } = new();
    public int TotalScu => Items.Sum(i => i.Scu);
}

// The two groupings the overlay/main window render: where to load, where to drop.
public sealed class Consolidation
{
    public List<ConsolidationStop> Pickups { get; } = new();
    public List<ConsolidationStop> Dropoffs { get; } = new();
}
```

- [ ] **Step 2: Verify the project compiles the new model**

Run: `cd /home/znoland/NexusApp && dotnet build NexusApp.Tests/NexusApp.Tests.csproj`
Expected: build succeeds (the test project references the main project, so this compiles `Haul.cs`).

- [ ] **Step 3: Commit** (ask Zach first per the git gate)

```bash
git add NexusApp/Models/Haul.cs
git commit -m "feat(hauling): add haul domain model"
```

---

### Task 2: HaulLogParser (pure line parsing)

**Files:**
- Create: `NexusApp/Services/HaulLogParser.cs`
- Test: `NexusApp.Tests/HaulLogParserTests.cs`

**Interfaces:**
- Consumes: nothing (pure static).
- Produces:
  - `record MarkerInfo(string MissionId, string Generator, string Contract, HaulRole Role, string CargoKey, int LegIndex, string ObjectiveId, double X, double Y, double Z)`
  - `record DeliverInfo(string MissionId, string ObjectiveId, string Commodity, int TargetScu, string Destination)`
  - `record AcceptInfo(string MissionId, string Title)`
  - `record CompletedInfo(string MissionId, string ObjectiveId)`
  - `record EndInfo(string MissionId, HaulOutcome Outcome)`
  - `static bool LooksHaulRelevant(string raw)`
  - `static MarkerInfo? ParseMarker(string raw)`
  - `static DeliverInfo? ParseDeliver(string raw)`
  - `static AcceptInfo? ParseContractAccepted(string raw)`
  - `static CompletedInfo? ParseObjectiveCompleted(string raw)`
  - `static EndInfo? ParseEndMission(string raw)`
  - `static string ParseTopology(string contract)`
  - `static string CompanyDisplay(string generator)`

- [ ] **Step 1: Write the failing tests**

`NexusApp.Tests/HaulLogParserTests.cs`:

```csharp
using NexusApp.Models;
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// Real (redacted) current-build (12061511) Game.log lines as fixtures, inline per repo convention.
public class HaulLogParserTests
{
    private const string MarkerPickup =
        "<2026-06-27T13:17:10.856Z> [Notice] <CLocalMissionPhaseMarker::CreateMarker> Creating objective marker: " +
        "missionId [a6c598e0-e8d2-4613-a7a3-e80f4c30d9c6], generator name [RedWind_Hauling], " +
        "contract [RedWind_Pyro_SupplyGrade_RegionA_CFP_StationToTradepost_Carbon_CargoHauling_AtoB_Intro], " +
        "contractDefinitionId[db9d9b2d-d850-4df1-ab0f-7fa2261e8f62], " +
        "objectiveId [pickup_6e9335e8-f62e-4eca-8fc5-a42ab1c3ab7d_0], markerEntityId [2806], zoneHostId [525246124367], " +
        "position [x: 2281204.528538, y: -14897443.959268, z: 2752554.224023] [Team_MissionFeatures][Missions]";

    private const string MarkerDropoff =
        "<2026-06-27T13:17:10.856Z> [Notice] <CLocalMissionPhaseMarker::CreateMarker> Creating objective marker: " +
        "missionId [a6c598e0-e8d2-4613-a7a3-e80f4c30d9c6], generator name [RedWind_Hauling], " +
        "contract [RedWind_Pyro_SupplyGrade_RegionA_CFP_StationToTradepost_Carbon_CargoHauling_AtoB_Intro], " +
        "contractDefinitionId[db9d9b2d-d850-4df1-ab0f-7fa2261e8f62], " +
        "objectiveId [dropoff_6e9335e8-f62e-4eca-8fc5-a42ab1c3ab7d_0], markerEntityId [2807], zoneHostId [525246124540], " +
        "position [x: 383115.366423, y: -245829.717381, z: -272467.223889] [Team_MissionFeatures][Missions]";

    private const string DeliverLine =
        "<2026-06-27T13:17:10.862Z> [Notice] <SHUDEvent_OnNotification> Added notification " +
        "\"New Objective: Deliver 0/158 SCU of Carbon to Jackson's Swap: \" [6] to queue. New queue size: 7, " +
        "MissionId: [a6c598e0-e8d2-4613-a7a3-e80f4c30d9c6], ObjectiveId: [dropoff_6e9335e8-f62e-4eca-8fc5-a42ab1c3ab7d_0] " +
        "[Team_CoreGameplayFeatures][Missions][Comms]";

    private const string AcceptRouteLine =
        "<2026-06-13T13:31:38.068Z> [Notice] <SHUDEvent_OnNotification> Added notification " +
        "\"Contract Accepted:  Master | <EM3>DIRECT</EM3> Bulk Haul | Ruin Station > Baijini Point <EM4>[BP]*</EM4>: \" [16] to queue. " +
        "New queue size: 8, MissionId: [a6c598e0-e8d2-4613-a7a3-e80f4c30d9c6], ObjectiveId: [] [Team_CoreGameplayFeatures][Missions][Comms]";

    private const string ObjectiveCompletedLine =
        "<2026-06-27T13:42:44.088Z> [Notice] <ObjectiveUpserted> Received ObjectiveUpserted push message for: " +
        "mission_id a6c598e0-e8d2-4613-a7a3-e80f4c30d9c6 - objective_id pickup_6e9335e8-f62e-4eca-8fc5-a42ab1c3ab7d_0 " +
        "- state MISSION_OBJECTIVE_STATE_COMPLETED - created 0 - flags=ShowInLog| [Team_GameServices][Missions]";

    private const string ObjectiveInProgressLine =
        "<2026-06-27T13:42:44.088Z> [Notice] <ObjectiveUpserted> Received ObjectiveUpserted push message for: " +
        "mission_id a6c598e0-e8d2-4613-a7a3-e80f4c30d9c6 - objective_id dropoff_6e9335e8-f62e-4eca-8fc5-a42ab1c3ab7d_0 " +
        "- state MISSION_OBJECTIVE_STATE_INPROGRESS - created 1 - flags=ShowInLog| [Team_GameServices][Missions]";

    private const string EndComplete =
        "<2026-06-13T13:35:51.224Z> [Notice] <EndMission> Ending mission for player. " +
        "MissionId[6ea3467c-83c8-4b41-b25c-ead2c558cc54] Player[REDACTED] PlayerId[REDACTED] " +
        "CompletionType[Complete] Reason[Mission Ended] [Team_MissionFeatures][Missions]";

    private const string EndAbandon =
        "<2026-06-27T13:30:27.252Z> [Notice] <EndMission> Ending mission for player. " +
        "MissionId[e179ea2b-3099-48a3-b0ef-6795bfb5337b] Player[REDACTED] PlayerId[REDACTED] " +
        "CompletionType[Abandon] Reason[Player left] [Team_MissionFeatures][Missions]";

    [Fact]
    public void ParseMarker_Pickup_ExtractsAllFields()
    {
        var m = HaulLogParser.ParseMarker(MarkerPickup);
        Assert.NotNull(m);
        Assert.Equal("a6c598e0-e8d2-4613-a7a3-e80f4c30d9c6", m!.MissionId);
        Assert.Equal("RedWind_Hauling", m.Generator);
        Assert.Equal(HaulRole.Pickup, m.Role);
        Assert.Equal("pickup_6e9335e8-f62e-4eca-8fc5-a42ab1c3ab7d_0", m.ObjectiveId);
        Assert.Equal("6e9335e8-f62e-4eca-8fc5-a42ab1c3ab7d_0", m.CargoKey);
        Assert.Equal(0, m.LegIndex);
        Assert.Equal(2281204.528538, m.X, 3);
    }

    [Fact]
    public void ParseMarker_Dropoff_RoleIsDropoff()
    {
        var m = HaulLogParser.ParseMarker(MarkerDropoff);
        Assert.Equal(HaulRole.Dropoff, m!.Role);
        Assert.Equal("6e9335e8-f62e-4eca-8fc5-a42ab1c3ab7d_0", m.CargoKey);
    }

    [Fact]
    public void ParseMarker_NonHaulContract_ReturnsNull()
    {
        var bounty = MarkerPickup.Replace(
            "contract [RedWind_Pyro_SupplyGrade_RegionA_CFP_StationToTradepost_Carbon_CargoHauling_AtoB_Intro]",
            "contract [BountyHuntersGuild_Bounty_Stanton_Medium_2]");
        Assert.Null(HaulLogParser.ParseMarker(bounty));
    }

    [Fact]
    public void ParseDeliver_ExtractsCommodityScuDestination()
    {
        var d = HaulLogParser.ParseDeliver(DeliverLine);
        Assert.NotNull(d);
        Assert.Equal("a6c598e0-e8d2-4613-a7a3-e80f4c30d9c6", d!.MissionId);
        Assert.Equal("dropoff_6e9335e8-f62e-4eca-8fc5-a42ab1c3ab7d_0", d.ObjectiveId);
        Assert.Equal("Carbon", d.Commodity);
        Assert.Equal(158, d.TargetScu);
        Assert.Equal("Jackson's Swap", d.Destination);
    }

    [Fact]
    public void ParseDeliver_NonScuObjective_ReturnsNull()
    {
        var dataObjective = DeliverLine.Replace("Deliver 0/158 SCU of Carbon to Jackson's Swap",
                                                "Deliver Energy Anomaly Data to Port Tressler's Freight Elevator");
        Assert.Null(HaulLogParser.ParseDeliver(dataObjective));
    }

    [Fact]
    public void ParseContractAccepted_StripsEmTags_AndKeepsRoute()
    {
        var a = HaulLogParser.ParseContractAccepted(AcceptRouteLine);
        Assert.NotNull(a);
        Assert.Equal("a6c598e0-e8d2-4613-a7a3-e80f4c30d9c6", a!.MissionId);
        Assert.DoesNotContain("<EM", a.Title);
        Assert.Contains("Ruin Station > Baijini Point", a.Title);
    }

    [Fact]
    public void ParseObjectiveCompleted_OnlyCompletedState()
    {
        var c = HaulLogParser.ParseObjectiveCompleted(ObjectiveCompletedLine);
        Assert.NotNull(c);
        Assert.Equal("pickup_6e9335e8-f62e-4eca-8fc5-a42ab1c3ab7d_0", c!.ObjectiveId);
        Assert.Null(HaulLogParser.ParseObjectiveCompleted(ObjectiveInProgressLine));
    }

    [Fact]
    public void ParseEndMission_MapsCompletionType_NoPii()
    {
        var done = HaulLogParser.ParseEndMission(EndComplete);
        Assert.Equal("6ea3467c-83c8-4b41-b25c-ead2c558cc54", done!.MissionId);
        Assert.Equal(HaulOutcome.Complete, done.Outcome);
        Assert.Equal(HaulOutcome.Abandoned, HaulLogParser.ParseEndMission(EndAbandon)!.Outcome);
    }

    [Theory]
    [InlineData("HaulCargo_AToB_NonMetal_Carbon_Stanton2_SmallGrade", "1 to 1")]
    [InlineData("RedWind_Pyro_..._CargoHauling_AtoB_Intro", "1 to 1")]
    [InlineData("HaulCargo_SingleToMulti2_Processed_AgriculturalSupplies_Stanton2_SmallGrade", "1 to 2")]
    [InlineData("HaulCargo_SingleToMulti3_Processed_Stims_Stanton2_SmallGrade1", "1 to 3")]
    [InlineData("HaulCargo_Multi2ToSingle_Waste_Waste_Stanton1_SupplyGrade", "2 to 1")]
    [InlineData("Something_Unrecognized", "Unknown")]
    public void ParseTopology_FromContractName(string contract, string expected) =>
        Assert.Equal(expected, HaulLogParser.ParseTopology(contract));

    [Theory]
    [InlineData("RedWind_Hauling", "Red Wind")]
    [InlineData("Covalex_Hauling", "Covalex")]
    public void CompanyDisplay_KnownGenerators(string generator, string expected) =>
        Assert.Equal(expected, HaulLogParser.CompanyDisplay(generator));
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `cd /home/znoland/NexusApp && dotnet test NexusApp.Tests/NexusApp.Tests.csproj --filter HaulLogParserTests`
Expected: compile error / FAIL ("HaulLogParser does not exist").

- [ ] **Step 3: Write the parser**

`NexusApp/Services/HaulLogParser.cs`:

```csharp
using System.Globalization;
using System.Text.RegularExpressions;
using NexusApp.Models;

namespace NexusApp.Services;

public record MarkerInfo(string MissionId, string Generator, string Contract, HaulRole Role,
                         string CargoKey, int LegIndex, string ObjectiveId, double X, double Y, double Z);
public record DeliverInfo(string MissionId, string ObjectiveId, string Commodity, int TargetScu, string Destination);
public record AcceptInfo(string MissionId, string Title);
public record CompletedInfo(string MissionId, string ObjectiveId);
public record EndInfo(string MissionId, HaulOutcome Outcome);

// Pure, stateless parsing of the haul-relevant Game.log line shapes. No file I/O, no PII:
// EndMission's Player[..]/PlayerId[..] are never read. See HaulLogParserTests for the line fixtures.
public static class HaulLogParser
{
    private static readonly Regex Marker = new(
        @"<CLocalMissionPhaseMarker::CreateMarker>.*?missionId \[(?<mid>[0-9a-f-]+)\].*?" +
        @"generator name \[(?<gen>[^\]]+)\].*?contract \[(?<contract>[^\]]+)\].*?" +
        @"objectiveId \[(?<role>pickup|dropoff)_(?<key>[0-9a-f-]+_(?<leg>\d+))\].*?" +
        @"position \[x: (?<x>[-0-9.]+), y: (?<y>[-0-9.]+), z: (?<z>[-0-9.]+)\]",
        RegexOptions.Compiled);

    private static readonly Regex Deliver = new(
        @"New Objective: Deliver \d+/(?<scu>\d+) SCU of (?<commodity>.+?) to (?<dest>.+?): "" .*?" +
        @"MissionId: \[(?<mid>[0-9a-f-]+)\], ObjectiveId: \[(?<oid>dropoff_[0-9a-f-]+_\d+)\]",
        RegexOptions.Compiled);

    private static readonly Regex Accept = new(
        @"Contract Accepted:\s+(?<title>.+?): "" .*?MissionId: \[(?<mid>[0-9a-f-]+)\]",
        RegexOptions.Compiled);

    private static readonly Regex Completed = new(
        @"<ObjectiveUpserted>.*?mission_id (?<mid>[0-9a-f-]+) - objective_id (?<oid>\S+) " +
        @"- state MISSION_OBJECTIVE_STATE_COMPLETED",
        RegexOptions.Compiled);

    private static readonly Regex End = new(
        @"<EndMission>.*?MissionId\[(?<mid>[0-9a-f-]+)\].*?CompletionType\[(?<ct>\w+)\]",
        RegexOptions.Compiled);

    private static readonly Regex EmTag = new(@"</?EM\d*>", RegexOptions.Compiled);

    // Cheap pre-filter so the tracker can skip the bulk of lines before running regex.
    public static bool LooksHaulRelevant(string raw) =>
        raw.Contains("HaulCargo") || raw.Contains("CargoHauling") ||
        raw.Contains("SCU of") || raw.Contains("Contract Accepted") ||
        raw.Contains("ObjectiveUpserted") || raw.Contains("EndMission");

    public static MarkerInfo? ParseMarker(string raw)
    {
        if (!raw.Contains("HaulCargo") && !raw.Contains("CargoHauling")) return null;
        var m = Marker.Match(raw);
        if (!m.Success) return null;
        var role = m.Groups["role"].Value == "pickup" ? HaulRole.Pickup : HaulRole.Dropoff;
        return new MarkerInfo(
            m.Groups["mid"].Value, m.Groups["gen"].Value, m.Groups["contract"].Value, role,
            m.Groups["key"].Value, int.Parse(m.Groups["leg"].Value),
            $"{m.Groups["role"].Value}_{m.Groups["key"].Value}",
            D(m.Groups["x"].Value), D(m.Groups["y"].Value), D(m.Groups["z"].Value));
    }

    public static DeliverInfo? ParseDeliver(string raw)
    {
        var m = Deliver.Match(raw);
        if (!m.Success) return null;
        return new DeliverInfo(m.Groups["mid"].Value, m.Groups["oid"].Value,
            m.Groups["commodity"].Value.Trim(), int.Parse(m.Groups["scu"].Value),
            m.Groups["dest"].Value.Trim());
    }

    public static AcceptInfo? ParseContractAccepted(string raw)
    {
        var m = Accept.Match(raw);
        if (!m.Success) return null;
        var title = EmTag.Replace(m.Groups["title"].Value, "").Replace("  ", " ").Trim();
        return new AcceptInfo(m.Groups["mid"].Value, title);
    }

    public static CompletedInfo? ParseObjectiveCompleted(string raw)
    {
        var m = Completed.Match(raw);
        return m.Success ? new CompletedInfo(m.Groups["mid"].Value, m.Groups["oid"].Value) : null;
    }

    public static EndInfo? ParseEndMission(string raw)
    {
        var m = End.Match(raw);
        if (!m.Success) return null;
        var outcome = m.Groups["ct"].Value switch
        {
            "Complete"   => HaulOutcome.Complete,
            "Abandon"    => HaulOutcome.Abandoned,
            "Fail"       => HaulOutcome.Failed,
            "Deactivate" => HaulOutcome.Deactivated,
            _            => HaulOutcome.Active,
        };
        return new EndInfo(m.Groups["mid"].Value, outcome);
    }

    public static string ParseTopology(string contract)
    {
        var c = contract.ToLowerInvariant();
        if (c.Contains("singletomulti2")) return "1 to 2";
        if (c.Contains("singletomulti3")) return "1 to 3";
        if (c.Contains("multi2tosingle")) return "2 to 1";
        if (c.Contains("atob")) return "1 to 1";
        return "Unknown";
    }

    public static string CompanyDisplay(string generator)
    {
        var name = generator.Replace("_Hauling", "").Replace("Hauling", "").Replace('_', ' ').Trim();
        // Split runs like "RedWind" -> "Red Wind".
        name = Regex.Replace(name, "(?<=[a-z])(?=[A-Z])", " ");
        return string.IsNullOrWhiteSpace(name) ? generator : name;
    }

    private static double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `cd /home/znoland/NexusApp && dotnet test NexusApp.Tests/NexusApp.Tests.csproj --filter HaulLogParserTests`
Expected: all PASS.

- [ ] **Step 5: Commit** (ask Zach first)

```bash
git add NexusApp/Services/HaulLogParser.cs NexusApp.Tests/HaulLogParserTests.cs
git commit -m "feat(hauling): add Game.log haul line parser with tests"
```

---

### Task 3: HaulTracker (stateful aggregation)

**Files:**
- Create: `NexusApp/Services/HaulTracker.cs`
- Test: `NexusApp.Tests/HaulTrackerTests.cs`

**Interfaces:**
- Consumes: `HaulLogParser` (Task 2), `GameLogEntry`/`GameLogWatcher` (existing), `Haul`/`HaulLeg`/`HaulOutcome` (Task 1).
- Produces:
  - `class HaulTracker : IDisposable`
  - `void Ingest(GameLogEntry e)` - public for headless tests, called by the watcher.
  - `void Reset()` - clears all hauls (new SC session).
  - `void Start(string path, bool fromBeginning = true)`, `void Stop()`, `bool IsRunning`, `string StartPath()`, `string PreferredPath { get; set; }`.
  - `IReadOnlyList<Haul> ActiveHauls`, `IReadOnlyList<Haul> FinishedHauls`, `IReadOnlyList<Haul> AllHauls`.
  - `event Action? Changed`, `event Action<Haul>? HaulEnded`.

- [ ] **Step 1: Write the failing tests**

`NexusApp.Tests/HaulTrackerTests.cs`:

```csharp
using NexusApp.Models;
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// Headless: feed real (redacted) lines straight into Ingest, no watcher tick, no real file.
public class HaulTrackerTests
{
    private const string Mid = "a6c598e0-e8d2-4613-a7a3-e80f4c30d9c6";

    private static GameLogEntry E(string raw) => new() { Raw = raw, Category = LogCategory.Other };

    private static readonly string MarkerPickup = HaulLogParserFixtures.MarkerPickup;
    private static readonly string MarkerDropoff = HaulLogParserFixtures.MarkerDropoff;
    private static readonly string DeliverLine = HaulLogParserFixtures.DeliverLine;
    private static readonly string PickupCompleted = HaulLogParserFixtures.ObjectiveCompletedLine;
    private const string EndComplete =
        "<2026-06-27T14:40:00.000Z> [Notice] <EndMission> Ending mission for player. " +
        "MissionId[a6c598e0-e8d2-4613-a7a3-e80f4c30d9c6] Player[REDACTED] PlayerId[REDACTED] " +
        "CompletionType[Complete] Reason[Mission Ended] [Team_MissionFeatures][Missions]";

    [Fact]
    public void Marker_CreatesHaul_WithCompanyAndTopologyAndLeg()
    {
        var t = new HaulTracker();
        t.Ingest(E(MarkerPickup));
        var h = Assert.Single(t.ActiveHauls);
        Assert.Equal(Mid, h.MissionId);
        Assert.Equal("Red Wind", h.Company);
        Assert.Equal("1 to 1", h.Topology);
        Assert.Single(h.Legs);
        Assert.Equal(HaulRole.Pickup, h.Legs[0].Role);
    }

    [Fact]
    public void DuplicateMarker_DoesNotDuplicateLeg()
    {
        var t = new HaulTracker();
        t.Ingest(E(MarkerPickup));
        t.Ingest(E(MarkerPickup));   // re-emitted on zone reload
        Assert.Single(t.ActiveHauls[0].Legs);
    }

    [Fact]
    public void Deliver_EnrichesDropoffLeg_WithCommodityScuDestination()
    {
        var t = new HaulTracker();
        t.Ingest(E(MarkerDropoff));
        t.Ingest(E(DeliverLine));
        var leg = t.ActiveHauls[0].Legs[0];
        Assert.Equal("Carbon", leg.Commodity);
        Assert.Equal(158, leg.TargetScu);
        Assert.Equal("Jackson's Swap", leg.Destination);
    }

    [Fact]
    public void ObjectiveCompleted_MarksLegDone()
    {
        var t = new HaulTracker();
        t.Ingest(E(MarkerPickup));
        t.Ingest(E(PickupCompleted));
        Assert.True(t.ActiveHauls[0].Legs[0].Completed);
    }

    [Fact]
    public void EndMission_MovesHaulToFinished_WithOutcome()
    {
        var t = new HaulTracker();
        t.Ingest(E(MarkerPickup));
        t.Ingest(E(EndComplete));
        Assert.Empty(t.ActiveHauls);
        var h = Assert.Single(t.FinishedHauls);
        Assert.Equal(HaulOutcome.Complete, h.Outcome);
    }

    [Fact]
    public void EndMission_ForUnknownNonHaulMission_Ignored()
    {
        var t = new HaulTracker();
        t.Ingest(E(EndComplete.Replace(Mid, "ffffffff-0000-0000-0000-000000000000")));
        Assert.Empty(t.AllHauls);
    }

    [Fact]
    public void Reset_ClearsEverything()
    {
        var t = new HaulTracker();
        t.Ingest(E(MarkerPickup));
        t.Reset();
        Assert.Empty(t.AllHauls);
    }

    [Fact]
    public void ChangedEvent_FiresOnNewHaul()
    {
        var t = new HaulTracker();
        int fired = 0;
        t.Changed += () => fired++;
        t.Ingest(E(MarkerPickup));
        Assert.True(fired >= 1);
    }
}
```

- [ ] **Step 2: Extract shared fixtures so both test classes reuse them**

Create `NexusApp.Tests/HaulLogParserFixtures.cs` and move every shared `const string` real-log line from `HaulLogParserTests` (MarkerPickup, MarkerDropoff, DeliverLine, AcceptRouteLine, ObjectiveCompletedLine, ObjectiveInProgressLine, EndComplete, EndAbandon) into it as `public const`. Update `HaulLogParserTests` to reference `HaulLogParserFixtures.MarkerPickup` etc. (DRY: one copy of each real line.)

- [ ] **Step 3: Run the tests to verify they fail**

Run: `cd /home/znoland/NexusApp && dotnet test NexusApp.Tests/NexusApp.Tests.csproj --filter HaulTrackerTests`
Expected: compile error / FAIL ("HaulTracker does not exist").

- [ ] **Step 4: Write the tracker**

`NexusApp/Services/HaulTracker.cs`:

```csharp
using NexusApp.Models;

namespace NexusApp.Services;

// BETA. App-lifetime owner of a Game.log watcher dedicated to cargo-hauling missions.
// Mirrors GameLogSession's shape (its own watcher; public Ingest for headless tests). Reads a
// game-authored file (read-only) and never extracts player identity. A mission id becomes a
// "haul" only when a HaulCargo/CargoHauling marker or a "Deliver N SCU" objective is seen; the
// generic ObjectiveUpserted/EndMission lines are applied only to known hauls so bounty/combat
// missions are ignored.
public sealed class HaulTracker : IDisposable
{
    private readonly GameLogWatcher _watcher = new();
    private readonly Dictionary<string, Haul> _byId = new();
    private readonly List<Haul> _order = new();   // insertion order for display

    public HaulTracker()
    {
        _watcher.LineAppended += Ingest;
        _watcher.LogReset += Reset;
    }

    public string PreferredPath { get; set; } = "";
    public bool IsRunning => _watcher.IsRunning;
    public string Path => _watcher.Path;

    public string StartPath()
    {
        if (!string.IsNullOrEmpty(Path) && System.IO.File.Exists(Path)) return Path;
        if (!string.IsNullOrEmpty(PreferredPath)) return PreferredPath;
        return GameLogWatcher.FindGameLog();
    }

    // Replay the current Game.log from the top so an app restart mid-session rebuilds active
    // hauls (parsing is idempotent: markers dedupe by objectiveId).
    public void Start(string path, bool fromBeginning = true) => _watcher.Start(path, fromBeginning);
    public void Stop() => _watcher.Stop();

    public IReadOnlyList<Haul> AllHauls => _order;
    public IReadOnlyList<Haul> ActiveHauls => _order.FindAll(h => h.IsActive);
    public IReadOnlyList<Haul> FinishedHauls => _order.FindAll(h => !h.IsActive);

    public event Action? Changed;
    public event Action<Haul>? HaulEnded;

    public void Reset()
    {
        _byId.Clear();
        _order.Clear();
        Changed?.Invoke();
    }

    public void Ingest(GameLogEntry e)
    {
        var raw = e.Raw;
        if (!HaulLogParser.LooksHaulRelevant(raw)) return;

        var marker = HaulLogParser.ParseMarker(raw);
        if (marker is not null) { ApplyMarker(marker); return; }

        var deliver = HaulLogParser.ParseDeliver(raw);
        if (deliver is not null) { ApplyDeliver(deliver); return; }

        var accept = HaulLogParser.ParseContractAccepted(raw);
        if (accept is not null && _byId.TryGetValue(accept.MissionId, out var ah))
        {
            ah.RouteTitle = accept.Title;
            ah.PickupName = DerivePickup(accept.Title);
            Changed?.Invoke();
            return;
        }

        var completed = HaulLogParser.ParseObjectiveCompleted(raw);
        if (completed is not null && _byId.TryGetValue(completed.MissionId, out var ch))
        {
            var leg = ch.LegByObjective(completed.ObjectiveId);
            if (leg is not null) { leg.Completed = true; Changed?.Invoke(); }
            return;
        }

        var end = HaulLogParser.ParseEndMission(raw);
        if (end is not null && _byId.TryGetValue(end.MissionId, out var eh))
        {
            eh.Outcome = end.Outcome;
            Logger.Info($"[HAUL] mission ended: {eh.Company} {eh.Topology} -> {end.Outcome}");
            HaulEnded?.Invoke(eh);
            Changed?.Invoke();
        }
    }

    private Haul GetOrCreate(string missionId)
    {
        if (_byId.TryGetValue(missionId, out var h)) return h;
        h = new Haul { MissionId = missionId };
        _byId[missionId] = h;
        _order.Add(h);
        return h;
    }

    private void ApplyMarker(MarkerInfo m)
    {
        var existed = _byId.ContainsKey(m.MissionId);
        var h = GetOrCreate(m.MissionId);
        h.Company = HaulLogParser.CompanyDisplay(m.Generator);
        h.ContractName = m.Contract;
        h.Topology = HaulLogParser.ParseTopology(m.Contract);
        if (h.LegByObjective(m.ObjectiveId) is null)
            h.Legs.Add(new HaulLeg
            {
                ObjectiveId = m.ObjectiveId, CargoKey = m.CargoKey, Role = m.Role,
                LegIndex = m.LegIndex, X = m.X, Y = m.Y, Z = m.Z,
            });
        if (!existed) Logger.Info($"[HAUL] mission accepted: {h.Company} {h.Topology}");
        Changed?.Invoke();
    }

    private void ApplyDeliver(DeliverInfo d)
    {
        var h = GetOrCreate(d.MissionId);
        var leg = h.LegByObjective(d.ObjectiveId);
        if (leg is null)
        {
            leg = new HaulLeg { ObjectiveId = d.ObjectiveId, Role = HaulRole.Dropoff };
            h.Legs.Add(leg);
        }
        leg.Commodity = d.Commodity;
        leg.TargetScu = d.TargetScu;
        leg.Destination = d.Destination;
        Changed?.Invoke();
    }

    // Best-effort: the left side of an "A > B" route title is the pickup location. Flavor titles
    // ("Red Wind Seeking New Haulers") have no route, so pickup name stays empty (handled in consolidation).
    private static string DerivePickup(string title)
    {
        var idx = title.IndexOf('>');
        if (idx <= 0) return "";
        var left = title[..idx];
        var bar = left.LastIndexOf('|');
        if (bar >= 0) left = left[(bar + 1)..];
        return left.Trim();
    }

    public void Dispose() => _watcher.Dispose();
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `cd /home/znoland/NexusApp && dotnet test NexusApp.Tests/NexusApp.Tests.csproj --filter HaulTrackerTests`
Expected: all PASS.

- [ ] **Step 6: Commit** (ask Zach first)

```bash
git add NexusApp/Services/HaulTracker.cs NexusApp.Tests/HaulTrackerTests.cs NexusApp.Tests/HaulLogParserFixtures.cs NexusApp.Tests/HaulLogParserTests.cs
git commit -m "feat(hauling): add HaulTracker session aggregation with tests"
```

---

### Task 4: Consolidation view (where to load / where to drop)

**Files:**
- Modify: `NexusApp/Services/HaulTracker.cs` (add `BuildConsolidation()`)
- Test: `NexusApp.Tests/HaulTrackerTests.cs` (add consolidation tests)

**Interfaces:**
- Produces on `HaulTracker`: `Consolidation BuildConsolidation()` - groups incomplete legs of active hauls by location. Dropoffs grouped by `Destination`. Pickups grouped by `Haul.PickupName` (fallback "Pickup (TBD)"), each pickup item borrowing commodity/SCU from its sibling dropoff via `CargoKey`.

- [ ] **Step 1: Write the failing tests** (append to `HaulTrackerTests`)

```csharp
    [Fact]
    public void Consolidation_GroupsActiveDropoffsByDestination()
    {
        var t = new HaulTracker();
        t.Ingest(E(MarkerDropoff));
        t.Ingest(E(DeliverLine));   // Carbon 158 -> Jackson's Swap
        var con = t.BuildConsolidation();
        var stop = Assert.Single(con.Dropoffs);
        Assert.Equal("Jackson's Swap", stop.Location);
        Assert.Equal(158, stop.TotalScu);
        Assert.Equal("Carbon", stop.Items[0].Commodity);
    }

    [Fact]
    public void Consolidation_PickupBorrowsCommodityFromSiblingDropoff()
    {
        var t = new HaulTracker();
        t.Ingest(E(MarkerPickup));    // pickup, CargoKey 6e9335e8-..._0
        t.Ingest(E(MarkerDropoff));   // dropoff, same CargoKey
        t.Ingest(E(DeliverLine));     // commodity/SCU land on the dropoff leg
        t.Ingest(E(HaulLogParserFixtures.AcceptRouteLine)); // route -> pickup name "Ruin Station"
        var con = t.BuildConsolidation();
        var pickup = Assert.Single(con.Pickups);
        Assert.Equal("Ruin Station", pickup.Location);
        Assert.Equal(158, pickup.TotalScu);   // borrowed from the sibling dropoff
    }

    [Fact]
    public void Consolidation_ExcludesCompletedLegs()
    {
        var t = new HaulTracker();
        t.Ingest(E(MarkerDropoff));
        t.Ingest(E(DeliverLine));
        // complete the dropoff
        t.Ingest(E(PickupCompleted.Replace("pickup_6e9335e8-f62e-4eca-8fc5-a42ab1c3ab7d_0",
                                           "dropoff_6e9335e8-f62e-4eca-8fc5-a42ab1c3ab7d_0")));
        Assert.Empty(t.BuildConsolidation().Dropoffs);
    }
```

Add `public const string AcceptRouteLine` to `HaulLogParserFixtures` (the route line from Task 2).

- [ ] **Step 2: Run to verify failure**

Run: `cd /home/znoland/NexusApp && dotnet test NexusApp.Tests/NexusApp.Tests.csproj --filter HaulTrackerTests`
Expected: FAIL ("BuildConsolidation does not exist").

- [ ] **Step 3: Implement `BuildConsolidation()`** (add to `HaulTracker`)

```csharp
    public Consolidation BuildConsolidation()
    {
        var con = new Consolidation();
        var pickups = new Dictionary<string, ConsolidationStop>();
        var dropoffs = new Dictionary<string, ConsolidationStop>();

        foreach (var h in _order)
        {
            if (!h.IsActive) continue;

            foreach (var leg in h.Legs)
            {
                if (leg.Completed) continue;

                if (leg.Role == HaulRole.Dropoff && leg.TargetScu > 0)
                    AddItem(dropoffs, leg.Destination, leg.Commodity, leg.TargetScu, h.MissionId);

                if (leg.Role == HaulRole.Pickup)
                {
                    // Borrow commodity/SCU from the sibling dropoff that shares this CargoKey.
                    var sib = h.Legs.Find(l => l.Role == HaulRole.Dropoff && l.CargoKey == leg.CargoKey);
                    var name = string.IsNullOrWhiteSpace(h.PickupName) ? "Pickup (TBD)" : h.PickupName;
                    AddItem(pickups, name, sib?.Commodity ?? "", sib?.TargetScu ?? 0, h.MissionId);
                }
            }
        }

        con.Pickups.AddRange(pickups.Values);
        con.Dropoffs.AddRange(dropoffs.Values);
        return con;

        static void AddItem(Dictionary<string, ConsolidationStop> map, string loc, string commodity, int scu, string mid)
        {
            if (string.IsNullOrWhiteSpace(loc)) return;
            if (!map.TryGetValue(loc, out var stop)) { stop = new ConsolidationStop { Location = loc }; map[loc] = stop; }
            stop.Items.Add((commodity, scu, mid));
        }
    }
```

- [ ] **Step 4: Run to verify pass**

Run: `cd /home/znoland/NexusApp && dotnet test NexusApp.Tests/NexusApp.Tests.csproj`
Expected: ALL tests pass (parser + tracker + consolidation + the pre-existing suite, ~74+ tests).

- [ ] **Step 5: Commit** (ask Zach first)

```bash
git add NexusApp/Services/HaulTracker.cs NexusApp.Tests/HaulTrackerTests.cs NexusApp.Tests/HaulLogParserFixtures.cs
git commit -m "feat(hauling): add load/drop consolidation view with tests"
```

---

### Task 5: App wiring (instantiate + replay + dispose)

**Files:**
- Modify: `NexusApp/App.xaml.cs` (static prop near `Data`/`Network`/`GameLog`; construct in `OnStartup` after `GameLog` block ~line 150; dispose in `OnExit` ~line 196)

**Interfaces:**
- Consumes: `HaulTracker` (Task 3).
- Produces: `App.Hauls` static singleton, started against the resolved Game.log path with `fromBeginning: true`.

- [ ] **Step 1: Read the real declaration + startup + exit regions**

Run: read `NexusApp/App.xaml.cs` lines 1-40 (to find the `public static ... GameLog` declaration), 113-151, and 193-201.

- [ ] **Step 2: Add the static property** next to the existing `public static GameLogSession GameLog` declaration:

```csharp
public static HaulTracker Hauls { get; private set; } = null!;
```

- [ ] **Step 3: Construct + start after the `GameLog.StateChanged` block (after ~line 150):**

```csharp
        // BETA Game.log cargo-hauling tracker. Own watcher (decoupled from the blueprint
        // session so haul tracking runs regardless of the blueprint toggle). Replays the
        // current Game.log from the top so a mid-session restart rebuilds active hauls.
        Hauls = new HaulTracker { PreferredPath = Settings.Current.GameLogPath };
        Hauls.HaulEnded += h => Views.ToastWindow.Show($"Haul {h.Outcome}: {h.Company}");
        Hauls.Start(Hauls.StartPath(), fromBeginning: true);
```

- [ ] **Step 4: Dispose in `OnExit` (alongside `GameLog?.Dispose()`):**

```csharp
        Hauls?.Dispose();
```

- [ ] **Step 5: Compile-verify the core still builds**

Run: `cd /home/znoland/NexusApp && dotnet test NexusApp.Tests/NexusApp.Tests.csproj`
Expected: PASS. (Full WPF app build happens in CI / on Zach's Windows box; it cannot build in WSL.)

- [ ] **Step 6: Commit** (ask Zach first)

```bash
git add NexusApp/App.xaml.cs
git commit -m "feat(hauling): wire HaulTracker into app lifetime"
```

---

### Task 6: `[HAUL]` logging is already wired

The `[HAUL]` tag is emitted from `HaulTracker.Ingest` (mission accepted, mission ended) in Task 3, so it appears in nexus.log and the App Log Monitor with no extra work. This satisfies the logging definition-of-done.

- [ ] **Step 1: Confirm** the two `Logger.Info("[HAUL] ...")` calls exist in `HaulTracker` and read sensibly. Add a third at leg completion if useful:

```csharp
// inside the ObjectiveCompleted branch, after leg.Completed = true:
Logger.Info($"[HAUL] leg complete: {ch.Company} {leg.Role} {leg.Commodity}");
```

- [ ] **Step 2: Run tests** (Logger.Info is safe headless; existing tests already call it).

Run: `cd /home/znoland/NexusApp && dotnet test NexusApp.Tests/NexusApp.Tests.csproj`
Expected: PASS.

- [ ] **Step 3: Commit** (ask Zach first)

```bash
git add NexusApp/Services/HaulTracker.cs
git commit -m "feat(hauling): log [HAUL] lifecycle to App Log Monitor"
```

---

### Task 7: Main-window Hauling domain tab

**Files:**
- Create: `NexusApp/Views/HaulingPage.cs` (code-built `UserControl`, mirror `Views/NetworkPage.cs`)
- Modify: `NexusApp/Views/MainWindow.xaml` (add `NavHauling` RadioButton after `NavNetwork`; add `PageHauling` host grid)
- Modify: `NexusApp/Views/MainWindow.xaml.cs` (`SetActivePage` wiring + `InitHaulingPage` lazy-load)

**Interfaces:**
- Consumes: `App.Hauls` (Task 5), `Haul`/`Consolidation` (Tasks 1, 4).
- Produces: a `HaulingPage` with a `Refresh()` method; a `"hauling"` nav tag.

- [ ] **Step 1: Read the real patterns** to copy exactly:
  - `NexusApp/Views/NetworkPage.cs` (whole file) - the code-built UserControl + theme-brush usage template.
  - `NexusApp/Views/MainWindow.xaml` lines 190-228 (`NavNetwork` RadioButton + `PageNetwork` host).
  - `NexusApp/Views/MainWindow.xaml.cs` lines 120-164 (`Nav_Click`, `SetActivePage`, `InitNetworkPage`).

- [ ] **Step 2: Create `HaulingPage.cs`** following `NetworkPage.cs` structure. Sections: (a) Active hauls list - per haul: `Company`, `Topology`, `RouteTitle`, and each leg as "Pickup/Dropoff: Commodity TargetScu SCU -> Destination [done]"; (b) Consolidation - two columns from `App.Hauls.BuildConsolidation()`: "Load at" (Pickups) and "Drop at" (Dropoffs), each stop showing Location + items + TotalScu; (c) Finished hauls list with outcome. Use `DynamicResource` brushes (`FgBrush`, `AccentBrush`, `Bg2Brush`, `BorderBrush`). Provide `public void Refresh()` that rebuilds from `App.Hauls`, and subscribe `App.Hauls.Changed += () => Dispatcher.Invoke(Refresh)` in the constructor. Empty-state text when no hauls: "No active hauls. Accept a hauling contract in-game.".

- [ ] **Step 3: Add the nav RadioButton** in `MainWindow.xaml` immediately after the `NavNetwork` block (copy NavNetwork's markup, rename to `x:Name="NavHauling"`, `Tag="hauling"`, label "Hauling", `Click="Nav_Click"`).

- [ ] **Step 4: Add the page host** in `MainWindow.xaml` after `PageNetwork`: `<Grid x:Name="PageHauling" Visibility="Collapsed"/>`.

- [ ] **Step 5: Wire `SetActivePage`** in `MainWindow.xaml.cs`: add `PageHauling.Visibility = page == "hauling" ? Visibility.Visible : Visibility.Collapsed;`, `NavHauling.IsChecked = page == "hauling";`, a title case `"hauling" => "Cargo Hauling"`, and `if (page == "hauling") InitHaulingPage();`.

- [ ] **Step 6: Add `InitHaulingPage()`** mirroring `InitNetworkPage()`:

```csharp
    private HaulingPage? _haulingPage;
    private void InitHaulingPage()
    {
        if (_haulingPage is null)
        {
            _haulingPage = new HaulingPage();
            PageHauling.Children.Add(_haulingPage);
        }
        _haulingPage.Refresh();
    }
```

- [ ] **Step 7: Compile-verify the core** (the WPF build is CI/Windows-only):

Run: `cd /home/znoland/NexusApp && dotnet test NexusApp.Tests/NexusApp.Tests.csproj`
Expected: PASS (no test regressions; UI is not unit-tested).

- [ ] **Step 8: Commit** (ask Zach first), then Zach build-verifies on Windows.

```bash
git add NexusApp/Views/HaulingPage.cs NexusApp/Views/MainWindow.xaml NexusApp/Views/MainWindow.xaml.cs
git commit -m "feat(hauling): add main-window Hauling domain tab"
```

---

### Task 8: Overlay Hauling glance tab

**Files:**
- Modify: `NexusApp/Views/OverlayWindow.xaml` (HAULING tab button + indicator + content grid)
- Modify: `NexusApp/Views/OverlayWindow.xaml.cs` (`TabHauling_Click`, `SwitchTab`, `RebuildHaulingPanel`, subscribe `App.Hauls.Changed`)

**Interfaces:**
- Consumes: `App.Hauls` (Task 5).
- Produces: a 5th overlay tab "HAULING" showing active hauls + the consolidation glance.

- [ ] **Step 1: Read the real patterns**:
  - `NexusApp/Views/OverlayWindow.xaml` lines 108-211 (tab button/indicator row + a content grid example, e.g. `StatsTabContent`).
  - `NexusApp/Views/OverlayWindow.xaml.cs` lines 1-100 (`_activeTab`, event subscriptions) and 385-650 (`SwitchTab`, `RebuildStatsPanel`).

- [ ] **Step 2: Add the tab button + indicator** in the tab row (add a column), named `TabHaulingBtn` / `TabHaulingIndicator`, content "HAULING", `Click="TabHauling_Click"`, copying the `TabStatsBtn` markup.

- [ ] **Step 3: Add the content grid** `<Grid x:Name="HaulingTabContent" Visibility="Collapsed"> ... </Grid>` after the last existing tab content, with a `StackPanel` host (e.g. `HaulingList`) that `RebuildHaulingPanel()` fills.

- [ ] **Step 4: Add handler + SwitchTab wiring** in `OverlayWindow.xaml.cs`:

```csharp
    private void TabHauling_Click(object sender, RoutedEventArgs e) => SwitchTab("hauling");
```

In `SwitchTab`, add (matching the existing per-tab pattern):

```csharp
        HaulingTabContent.Visibility = tab == "hauling" ? Visibility.Visible : Visibility.Collapsed;
        TabHaulingIndicator.Background = tab == "hauling" ? accent : Brushes.Transparent;
        if (tab == "hauling") RebuildHaulingPanel();
```

- [ ] **Step 5: Add `RebuildHaulingPanel()`** that clears `HaulingList` and renders, from `App.Hauls`: a count row ("Active hauls: N"), each active haul (Company - Topology, each incomplete leg "Load/Drop Commodity Scu SCU @ Location"), then a compact consolidation summary from `App.Hauls.BuildConsolidation()` ("Drop at Baijini Point: 254 SCU"). Empty-state: "No active hauls.". Use `FindResource` theme brushes as the STATS tab does.

- [ ] **Step 6: Subscribe to live updates** in the constructor (near the existing `App.GameLog.*` subscriptions):

```csharp
        App.Hauls.Changed += () => { if (_activeTab == "hauling") RebuildHaulingPanel(); };
```

- [ ] **Step 7: Compile-verify the core**:

Run: `cd /home/znoland/NexusApp && dotnet test NexusApp.Tests/NexusApp.Tests.csproj`
Expected: PASS.

- [ ] **Step 8: Commit** (ask Zach first), then Zach runtime-verifies the overlay on Windows.

```bash
git add NexusApp/Views/OverlayWindow.xaml NexusApp/Views/OverlayWindow.xaml.cs
git commit -m "feat(hauling): add overlay Hauling glance tab"
```

---

## Final verification (before any merge/release - both need explicit yes from Zach)

- [ ] `cd /home/znoland/NexusApp && dotnet test NexusApp.Tests/NexusApp.Tests.csproj` - all green.
- [ ] Push branch (ask first) so CI build-check compiles the full WPF app.
- [ ] Zach runtime-verifies on Windows: accept a hauling contract in-game, confirm the main-window Hauling tab and the overlay HAULING tab populate, legs flip to done, the consolidation groupings read correctly, a completed haul moves to Finished, and `[HAUL]` lines show in the App Log Monitor.
- [ ] Confirm no PII (handle/GEID) appears anywhere in the UI or nexus.log.
- [ ] Decide finish path via superpowers:finishing-a-development-branch.

## Known v1 limitations (by design, from the locked Game.log contract)

- No live partial SCU progress (the log only ever shows `0/M`); legs are binary done/not-done.
- No aUEC payout / earnings (not logged).
- Pickup location names are best-effort from the route title; if a flavor title has no "A > B" route, pickups group under "Pickup (TBD)". Validate against more completed-haul logs during runtime testing; refine `DerivePickup` if needed.
- Multi-leg (`SingleToMulti2/3`, `Multi2ToSingle`) is modeled but was only seen partially in recon; confirm leg pairing on a real multi-dropoff haul during runtime testing.
