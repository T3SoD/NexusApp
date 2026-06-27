# Server/Shard Display Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:subagent-driven-development. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Show the current Star Citizen server/shard and the last 3 shards on the overlay STATS tab, driven from Game.log `<Join PU>` lines, with the rolling list persisted to settings.

**Architecture:** A pure static `ShardLogParser` parses `<Join PU>` lines into a `ShardSession`. A standalone `ShardTracker` (mirrors `HaulTracker`: owns its own `GameLogWatcher`, public `Ingest(GameLogEntry)` for headless tests) keeps the rolling history (current + last 3), dedupes consecutive re-joins onto the same shard, and persists via injected load/save callbacks (wired to settings.json in `App`). The overlay STATS tab renders a "current card + recent list" at the top.

**Tech Stack:** C# / .NET 8 (net8.0-windows), WPF, xUnit. Reuses `GameLogWatcher`, `Logger`. Built on branch `feature/cargo-hauling` (same branch as the hauling feature, per Zach).

## Global Constraints

- Branch `feature/cargo-hauling`. Commits/push/merge need Zach's explicit per-action yes.
- No emojis, no em-dashes (hyphens only) anywhere. No Claude attribution.
- No PII: `<Join PU>` carries only the game SERVER ip (`address[...]`), not the player. Never read player handle/GEID.
- `[SHARD]` tag logged to nexus.log (App Log Monitor visibility, definition-of-done).
- Cannot build/test in WSL (no dotnet; net8.0-windows/WPF). Verified on Windows (`~/test_nexus.ps1`) + CI.

## Game.log signal (verified, current build 12061511)

`<2026-06-27T13:14:51.882Z> [Notice] <Join PU> address[34.181.129.126] port[64318] shard[pub_use1b_12030094_140] locationId[...] [Team_GameServices][GIM][Matchmaking]`

- Fires on every shard join (relog / server transition). The leading `<...>` is the ISO-8601 UTC join time.
- shard = `<env>_<regionzone>_<build>_<instance>`: `pub_use1b_12030094_140` -> env `pub`, regionzone `use1b`, build `12030094`, instance `140`.
- Region decode (alpha prefix before the digit): use->US East, usw->US West, euw->EU West, euc->EU Central, eu->EU, apse->Asia SE, apne->Asia NE, ape->Asia E, aps->Asia S, aus->Australia, sae->S. America; unknown -> the raw code uppercased. Observed in Zach's logs: use1b, euw1b, apse2a, ape1a.
- A second variant `{Join PU} [0] id[..] status[..] port[..]` (curly braces) has NO shard - ignore it (match only `<Join PU>` with `address[`+`shard[`).

## File Structure

**New (testable core, Tasks 1-2):**
- `NexusApp/Models/ShardSession.cs` - the shard record.
- `NexusApp/Services/ShardLogParser.cs` - pure static parser + region decode.
- `NexusApp/Services/ShardTracker.cs` - stateful rolling history + persistence callbacks + own watcher.
- `NexusApp.Tests/ShardLogParserTests.cs`, `NexusApp.Tests/ShardTrackerTests.cs`.

**Modified (Tasks 3-4):**
- `NexusApp/Models/AppSettings.cs` - add `RecentShards` list (additive, no schema bump).
- `NexusApp/App.xaml.cs` - construct `App.Shards`, start (replay), dispose.
- `NexusApp/Views/OverlayWindow.xaml` + `.xaml.cs` - shard section at the top of the STATS tab.

---

### Task 1: ShardSession model + ShardLogParser

**Files:**
- Create: `NexusApp/Models/ShardSession.cs`, `NexusApp/Services/ShardLogParser.cs`
- Test: `NexusApp.Tests/ShardLogParserTests.cs`

**Interfaces:**
- Produces: `NexusApp.Models.ShardSession { string ShardId; string Region; string RegionCode; string Instance; string ServerIp; DateTime JoinedAt; }` (all `init`).
- `static ShardSession? ShardLogParser.ParseJoin(string raw)` (parses the leading ISO timestamp into JoinedAt UTC; falls back to DateTime.UtcNow if absent).
- `static string ShardLogParser.DecodeRegion(string regionCode)`.

- [ ] **Step 1: Write the failing tests** (`ShardLogParserTests.cs`):

```csharp
using NexusApp.Models;
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

public class ShardLogParserTests
{
    private const string UseJoin =
        "<2026-06-27T13:14:51.882Z> [Notice] <Join PU> address[34.181.129.126] port[64318] " +
        "shard[pub_use1b_12030094_140] locationId[844429225164801] [Team_GameServices][GIM][Matchmaking]";

    private const string EuJoin =
        "<2026-06-27T08:01:00.000Z> [Notice] <Join PU> address[130.211.53.99] port[64311] " +
        "shard[pub_euw1b_11704877_070] locationId[844429225164801] [Team_GameServices][GIM][Matchmaking]";

    private const string CurlyJoin =
        "<2026-06-27T13:14:51.882Z> [+] [CIG] {Join PU} [0] id[600d1825-286f-4f61-8e9c-2732697b766b] status[1] port[64318]";

    [Fact]
    public void ParseJoin_ExtractsAllFields()
    {
        var s = ShardLogParser.ParseJoin(UseJoin);
        Assert.NotNull(s);
        Assert.Equal("pub_use1b_12030094_140", s!.ShardId);
        Assert.Equal("use1b", s.RegionCode);
        Assert.Equal("US East", s.Region);
        Assert.Equal("140", s.Instance);
        Assert.Equal("34.181.129.126", s.ServerIp);
        Assert.Equal(2026, s.JoinedAt.Year);
        Assert.Equal(DateTimeKind.Utc, s.JoinedAt.Kind);
    }

    [Fact]
    public void ParseJoin_DecodesEuRegion()
    {
        var s = ShardLogParser.ParseJoin(EuJoin);
        Assert.Equal("EU West", s!.Region);
        Assert.Equal("070", s.Instance);
    }

    [Fact]
    public void ParseJoin_CurlyVariant_ReturnsNull() => Assert.Null(ShardLogParser.ParseJoin(CurlyJoin));

    [Fact]
    public void ParseJoin_NonJoinLine_ReturnsNull() =>
        Assert.Null(ShardLogParser.ParseJoin("<2026-06-27T13:14:51.882Z> [Notice] <Something Else> foo"));

    [Theory]
    [InlineData("use1b", "US East")]
    [InlineData("usw2a", "US West")]
    [InlineData("euw1b", "EU West")]
    [InlineData("apse2a", "Asia SE")]
    [InlineData("ape1a", "Asia E")]
    [InlineData("aus1a", "Australia")]
    [InlineData("zzz9z", "ZZZ9Z")]
    public void DecodeRegion_MapsKnownPrefixes(string code, string expected) =>
        Assert.Equal(expected, ShardLogParser.DecodeRegion(code));
}
```

- [ ] **Step 2: Run to verify failure** - `cd /home/znoland/NexusApp && dotnet test NexusApp.Tests/NexusApp.Tests.csproj --filter ShardLogParserTests` -> FAIL (does not exist). (NOTE: dotnet is unavailable in WSL; the implementer writes the test, the run happens on Windows/CI.)

- [ ] **Step 3: Write `ShardSession.cs`:**

```csharp
namespace NexusApp.Models;

// One Star Citizen shard the player joined, parsed from a Game.log <Join PU> line.
// ServerIp is the game server address (matchmaking-assigned), not the player - no PII.
public sealed class ShardSession
{
    public string ShardId { get; init; } = "";      // pub_use1b_12030094_140
    public string Region { get; init; } = "";        // decoded, e.g. "US East"
    public string RegionCode { get; init; } = "";    // raw, e.g. "use1b"
    public string Instance { get; init; } = "";      // "140"
    public string ServerIp { get; init; } = "";      // 34.181.129.126
    public DateTime JoinedAt { get; init; } = DateTime.UtcNow;
}
```

- [ ] **Step 4: Write `ShardLogParser.cs`:**

```csharp
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using NexusApp.Models;

namespace NexusApp.Services;

// Pure parsing of the Game.log "<Join PU>" shard line. No PII (server ip only).
public static class ShardLogParser
{
    private static readonly Regex Join = new(
        @"^<(?<ts>[0-9T:.Z+-]+)>.*?<Join PU> address\[(?<ip>[0-9.]+)\] port\[\d+\] " +
        @"shard\[(?<shard>(?:pub|priv)_(?<region>[a-z]+\d+[a-z])_\d+_(?<instance>\d+))\]",
        RegexOptions.Compiled);

    public static ShardSession? ParseJoin(string raw)
    {
        var m = Join.Match(raw);
        if (!m.Success) return null;
        var when = DateTimeOffset.TryParse(m.Groups["ts"].Value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto)
            ? dto.UtcDateTime : DateTime.UtcNow;
        var code = m.Groups["region"].Value;
        return new ShardSession
        {
            ShardId = m.Groups["shard"].Value,
            RegionCode = code,
            Region = DecodeRegion(code),
            Instance = m.Groups["instance"].Value,
            ServerIp = m.Groups["ip"].Value,
            JoinedAt = when,
        };
    }

    public static string DecodeRegion(string regionCode)
    {
        var prefix = new string(regionCode.TakeWhile(char.IsLetter).ToArray());
        return prefix switch
        {
            "use"  => "US East",
            "usw"  => "US West",
            "euw"  => "EU West",
            "euc"  => "EU Central",
            "eu"   => "EU",
            "apse" => "Asia SE",
            "apne" => "Asia NE",
            "ape"  => "Asia E",
            "aps"  => "Asia S",
            "aus"  => "Australia",
            "sae"  => "S. America",
            _      => regionCode.ToUpperInvariant(),
        };
    }
}
```

- [ ] **Step 5: Verify by inspection** (no compiler): trace the `Join` regex against `UseJoin` (ts, ip, shard, region=use1b, instance=140) and `EuJoin`; confirm `CurlyJoin` fails (no `<Join PU> address[`); confirm `DecodeRegion` TakeWhile-letter yields use/usw/euw/apse/ape/aus and the fallback uppercases unknown.

- [ ] **Step 6: Commit** (ask Zach): `git add NexusApp/Models/ShardSession.cs NexusApp/Services/ShardLogParser.cs NexusApp.Tests/ShardLogParserTests.cs && git commit -m "feat(shard): add <Join PU> parser + region decode with tests"`

---

### Task 2: ShardTracker

**Files:**
- Create: `NexusApp/Services/ShardTracker.cs`
- Test: `NexusApp.Tests/ShardTrackerTests.cs`

**Interfaces:**
- Consumes: `ShardLogParser` (Task 1), `GameLogWatcher`/`GameLogEntry` (existing).
- Produces: `class ShardTracker : IDisposable` with ctor `(Func<IEnumerable<ShardSession>> loadPersisted, Action<IReadOnlyList<ShardSession>> savePersisted)`; `void Ingest(GameLogEntry e)`; `ShardSession? Current`; `IReadOnlyList<ShardSession> Recent` (the 3 before current); `IReadOnlyList<ShardSession> All`; `event Action? Changed`; `string PreferredPath {get;set;}`, `string StartPath()`, `void Start(string,bool=true)`, `void Stop()`, `bool IsRunning`.

- [ ] **Step 1: Write the failing tests** (`ShardTrackerTests.cs`):

```csharp
using NexusApp.Models;
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

public class ShardTrackerTests
{
    private static GameLogEntry E(string raw) => new() { Raw = raw, Category = LogCategory.Other };
    private static string Join(string shard, string ip = "10.0.0.1") =>
        $"<2026-06-27T13:14:51.882Z> [Notice] <Join PU> address[{ip}] port[64318] shard[{shard}] locationId[1] [x]";

    private static ShardTracker Make(out List<ShardSession> saved, params ShardSession[] persisted)
    {
        var store = persisted.ToList();
        var captured = store;
        var t = new ShardTracker(() => captured, list => { captured = list.ToList(); });
        saved = captured;   // note: reassigned on save; tests re-read via tracker.All
        return t;
    }

    [Fact]
    public void FirstJoin_SetsCurrent_RecentEmpty()
    {
        var t = new ShardTracker(() => new List<ShardSession>(), _ => { });
        t.Ingest(E(Join("pub_use1b_12030094_140")));
        Assert.Equal("pub_use1b_12030094_140", t.Current!.ShardId);
        Assert.Empty(t.Recent);
    }

    [Fact]
    public void SecondDistinctJoin_PushesPriorToRecent()
    {
        var t = new ShardTracker(() => new List<ShardSession>(), _ => { });
        t.Ingest(E(Join("pub_use1b_12030094_140")));
        t.Ingest(E(Join("pub_use1b_12030094_150")));
        Assert.Equal("pub_use1b_12030094_150", t.Current!.ShardId);
        Assert.Single(t.Recent);
        Assert.Equal("pub_use1b_12030094_140", t.Recent[0].ShardId);
    }

    [Fact]
    public void RejoinSameShard_NoChange()
    {
        int changed = 0;
        var t = new ShardTracker(() => new List<ShardSession>(), _ => { });
        t.Changed += () => changed++;
        t.Ingest(E(Join("pub_use1b_12030094_140")));
        t.Ingest(E(Join("pub_use1b_12030094_140")));   // same shard, e.g. reconnect
        Assert.Single(t.All);
        Assert.Equal(1, changed);
    }

    [Fact]
    public void HistoryCappedAtFour_CurrentPlusThree()
    {
        var t = new ShardTracker(() => new List<ShardSession>(), _ => { });
        foreach (var n in new[] { "010", "020", "030", "040", "050" })
            t.Ingest(E(Join($"pub_use1b_12030094_{n}")));
        Assert.Equal(4, t.All.Count);
        Assert.Equal("pub_use1b_12030094_050", t.Current!.ShardId);
        Assert.Equal(3, t.Recent.Count);
        Assert.DoesNotContain(t.All, s => s.Instance == "010");
    }

    [Fact]
    public void Save_InvokedWithHistory_OnEachNewShard()
    {
        IReadOnlyList<ShardSession>? lastSaved = null;
        var t = new ShardTracker(() => new List<ShardSession>(), list => lastSaved = list);
        t.Ingest(E(Join("pub_use1b_12030094_140")));
        Assert.NotNull(lastSaved);
        Assert.Equal("pub_use1b_12030094_140", lastSaved![0].ShardId);
    }

    [Fact]
    public void Load_RestoresPersistedHistory()
    {
        var persisted = new List<ShardSession> { new() { ShardId = "pub_euw1b_1_99", Instance = "99" } };
        var t = new ShardTracker(() => persisted, _ => { });
        Assert.Equal("pub_euw1b_1_99", t.Current!.ShardId);
    }

    [Fact]
    public void NonJoinLine_Ignored()
    {
        var t = new ShardTracker(() => new List<ShardSession>(), _ => { });
        t.Ingest(E("<2026-06-27T13:14:51.882Z> [Notice] <Something> foo"));
        Assert.Null(t.Current);
    }
}
```

- [ ] **Step 2: Run to verify failure** (Windows/CI).

- [ ] **Step 3: Write `ShardTracker.cs`:**

```csharp
using System.Linq;
using NexusApp.Models;

namespace NexusApp.Services;

// App-lifetime tracker of the current shard + recent shard history, parsed from Game.log <Join PU>
// lines. Mirrors HaulTracker (own GameLogWatcher, public Ingest for headless tests). Persistence is
// injected so the rolling list survives app/SC relaunches. No PII (server ip only). History is NOT
// cleared on a new SC session (LogReset) - the point is to remember across sessions.
public sealed class ShardTracker : IDisposable
{
    private const int MaxKeep = 4;   // current + last 3
    private readonly GameLogWatcher _watcher = new();
    private readonly List<ShardSession> _history = new();   // newest first; current = [0]
    private readonly Action<IReadOnlyList<ShardSession>> _save;

    public ShardTracker(Func<IEnumerable<ShardSession>> loadPersisted, Action<IReadOnlyList<ShardSession>> savePersisted)
    {
        _save = savePersisted;
        _history.AddRange(loadPersisted() ?? Enumerable.Empty<ShardSession>());
        _watcher.LineAppended += Ingest;
        // intentionally NOT subscribing LogReset: shard history persists across SC sessions.
    }

    public ShardSession? Current => _history.Count > 0 ? _history[0] : null;
    public IReadOnlyList<ShardSession> Recent => _history.Skip(1).Take(3).ToList();
    public IReadOnlyList<ShardSession> All => _history;

    public event Action? Changed;

    public string PreferredPath { get; set; } = "";
    public bool IsRunning => _watcher.IsRunning;
    public string Path => _watcher.Path;

    public string StartPath()
    {
        if (!string.IsNullOrEmpty(Path) && System.IO.File.Exists(Path)) return Path;
        if (!string.IsNullOrEmpty(PreferredPath)) return PreferredPath;
        return GameLogWatcher.FindGameLog();
    }

    public void Start(string path, bool fromBeginning = true) => _watcher.Start(path, fromBeginning);
    public void Stop() => _watcher.Stop();

    public void Ingest(GameLogEntry e)
    {
        if (!e.Raw.Contains("<Join PU>")) return;
        var s = ShardLogParser.ParseJoin(e.Raw);
        if (s is null) return;
        if (_history.Count > 0 && _history[0].ShardId == s.ShardId) return;   // dedupe consecutive re-joins
        _history.Insert(0, s);
        while (_history.Count > MaxKeep) _history.RemoveAt(_history.Count - 1);
        _save(_history);
        Logger.Info($"[SHARD] joined {s.Region} shard {s.Instance} ({s.ShardId})");
        Changed?.Invoke();
    }

    public void Dispose() => _watcher.Dispose();
}
```

- [ ] **Step 4: Run to verify pass** (Windows/CI).

- [ ] **Step 5: Verify by inspection:** trace each test; confirm dedupe guard (`_history[0].ShardId == s.ShardId`), cap loop, save-on-change, load-in-ctor, `Recent` = items 1..3.

- [ ] **Step 6: Commit** (ask Zach): `git add NexusApp/Services/ShardTracker.cs NexusApp.Tests/ShardTrackerTests.cs && git commit -m "feat(shard): add ShardTracker rolling history with tests"`

---

### Task 3: Persistence + App wiring

**Files:**
- Modify: `NexusApp/Models/AppSettings.cs` (add `RecentShards`)
- Modify: `NexusApp/App.xaml.cs` (static `Shards`, construct, start, dispose)

**Interfaces:**
- Consumes: `ShardTracker` (Task 2).
- Produces: `App.Shards` static singleton; `AppSettings.RecentShards`.

- [ ] **Step 1: Add the settings field** in `AppSettings.cs` (additive, defaults to empty so old settings.json load fine - no SettingsSchemaVersion bump; confirm by reading the migration logic in SettingsService):

```csharp
    // Server/shard display: rolling current + last 3 shards (most recent first), persisted so the
    // RECENT list survives app/SC relaunches. Populated from Game.log <Join PU> lines.
    public List<ShardSession> RecentShards { get; set; } = [];
```

- [ ] **Step 2: Read** `NexusApp/App.xaml.cs` lines 17-21 (static service block, where `public static HaulTracker Hauls` was added) and 151-165 (where `Hauls` is constructed) and the `OnExit` disposes.

- [ ] **Step 3: Add the static property** next to `Hauls`:

```csharp
    public static ShardTracker Shards { get; private set; } = null!;
```

- [ ] **Step 4: Construct after the `Hauls` block in OnStartup:**

```csharp
        // Server/shard tracker. Own watcher (always on); persists the rolling list to settings so the
        // RECENT shards survive relaunches. Replays the current Game.log for this session's joins.
        Shards = new ShardTracker(
            () => Settings.Current.RecentShards,
            list => { Settings.Current.RecentShards = list.ToList(); Settings.Save(); })
        { PreferredPath = Settings.Current.GameLogPath };
        Shards.Start(Shards.StartPath(), fromBeginning: true);
```

- [ ] **Step 5: Dispose in OnExit** (alongside `Hauls?.Dispose();`): `Shards?.Dispose();`

- [ ] **Step 6: Verify by inspection:** `Settings.Current.RecentShards` round-trips (List<ShardSession>, STJ serializes init props in .NET 8); `using System.Linq;` present for `.ToList()` (it is, used by Hauls block). Confirm no SettingsSchemaVersion migration is required for an additive defaulted field.

- [ ] **Step 7: Commit** (ask Zach): `git add NexusApp/Models/AppSettings.cs NexusApp/App.xaml.cs && git commit -m "feat(shard): persist recent shards + wire ShardTracker into app"`

---

### Task 4: Overlay STATS shard section

**Files:**
- Modify: `NexusApp/Views/OverlayWindow.xaml` (a shard panel at the TOP of `StatsTabContent`)
- Modify: `NexusApp/Views/OverlayWindow.xaml.cs` (`RebuildShardPanel`, subscribe `App.Shards.Changed`, refresh on STATS tab show)

**Interfaces:**
- Consumes: `App.Shards` (Task 3).

- [ ] **Step 1: Read** `NexusApp/Views/OverlayWindow.xaml` `StatsTabContent` (~lines 156-211) and `OverlayWindow.xaml.cs` `RebuildStatsPanel` (~602) + the constructor subscriptions (~58-84) + `SwitchTab` STATS dispatch. Mirror the existing STATS row-building idiom (brush keys: FgBrush/FgDimBrush/AccentBrush/NavBorderBrush/Bg2NavBrush; the cached brush helper).

- [ ] **Step 2:** Add a host panel `StackPanel x:Name="ShardPanel"` at the TOP of `StatsTabContent` (above the existing Track-Session controls), and have a new `RebuildShardPanel()` fill it. Render the chosen mockup:
  - Section header "SERVER / SHARD".
  - Current card (Border, Bg2NavBrush/NavBorderBrush): big line `"{Current.Region}  -  Shard {Current.Instance}"` (FgBrush, Head font), small line `Current.ShardId` (FgDimBrush, Mono). If `App.Shards.Current` is null: a muted "Not connected to a shard yet." line and no recent list.
  - "RECENT" subheader, then up to 3 rows from `App.Shards.Recent`: `"{Ago(s.JoinedAt)}   {s.Region} - {s.Instance}"` (Mono, FgDimBrush), where `Ago` formats `DateTime.UtcNow - s.JoinedAt` as "Nm ago"/"Nh ago"/"Nd ago".
- [ ] **Step 3:** Call `RebuildShardPanel()` from the STATS-tab rebuild path (where `RebuildStatsPanel()` is invoked on tab show) and subscribe in the constructor: `App.Shards.Changed += OnShardsChanged;` with a named handler `private void OnShardsChanged() { if (_activeTab == "stats") RebuildShardPanel(); }`, and detach it in `OnClosed` (mirror the `OnHaulsChanged` / `App.GameLog.*` detach pattern).
- [ ] **Step 4: Constraints:** no emojis/em-dashes; only brush keys already used in STATS; server ip optional (the chosen mockup omits it); no PII.
- [ ] **Step 5: Compile-verify core** (`dotnet test` on Windows/CI; WPF cannot build in WSL).
- [ ] **Step 6: Commit** (ask Zach): `git add NexusApp/Views/OverlayWindow.xaml NexusApp/Views/OverlayWindow.xaml.cs && git commit -m "feat(shard): show current + recent shards on the STATS overlay tab"`

---

## Final verification (Windows/CI, before merge - needs Zach's yes)
- `~/test_nexus.ps1` - build + `dotnet test` (new ShardLogParser + ShardTracker tests green).
- Runtime: relog / change shard in-game; confirm the STATS tab shows the current shard card + decoded region, RECENT fills as you hop shards, the list survives an app restart, `[SHARD]` lines appear in the App Log Monitor, and no PII shows.

## Known v1 limits
- Relative times ("2m ago") recompute on panel rebuild (tab show / shard change), not on a live ticking timer. Add a timer later if desired.
- Region decode covers observed + common prefixes; unknown prefixes show the raw code uppercased (still informative).
