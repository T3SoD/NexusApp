# Cargo Contract OCR Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Auto-import in-game Contracts-panel details (reward, contractor, commodity/SCU/pickup/dropoff) via OCR and enrich the matching Game.log-tracked haul by title.

**Architecture:** A pure static `ContractParser` (TDD core) turns OCR panel text into `ContractDetails`. A `ContractOcrService` (its own `Windows.Media.Ocr` engine + GDI capture + lighter text-panel preprocessing - the locked RS `OcrService` is untouched) produces that text. A `ContractScanner` polls the saved contract region (~1s, toggle-gated), dedupes by title, and raises `ContractScanned`. App wires that to `HaulTracker.ApplyContractDetails`, which matches by normalized title and enriches the haul. UI adds a region button + auto-scan toggle and shows the enriched fields.

**Tech Stack:** C#/.NET 8 (net8.0-windows), WPF, `Windows.Media.Ocr` (WinRT), xUnit. Branch `feature/cargo-hauling`. Design: `docs/superpowers/specs/2026-06-27-cargo-contract-ocr-design.md`.

## Global Constraints

- Branch `feature/cargo-hauling`. Commits/push/merge need Zach's explicit per-action yes.
- No emojis, no em-dashes (hyphens only). No Claude attribution.
- The player handle must NEVER appear in code, fixtures, or commits. The parser extracts only title/reward/contractor/objectives.
- Do NOT modify the locked RS path: `OcrService.Preprocess`/`ExtractRsValue`/`CaptureRegion`/`ScanFullScreenAsync`. Contract OCR is a separate service.
- `[CONTRACT]` tag logged to nexus.log (App Log Monitor, definition-of-done).
- Cannot build/test WPF in WSL (no dotnet; net8.0-windows). Parser/matcher tests run on Windows/CI; OCR/scanner/UI runtime-verified by Zach.

## File Structure

**New (testable core, Tasks 1-3):**
- `NexusApp/Models/ContractDetails.cs` - `ContractObjective`, `ContractDetails`.
- `NexusApp/Services/ContractParser.cs` - pure static OCR-text parser + `NormalizeTitle`.
- `NexusApp.Tests/ContractParserTests.cs`, `NexusApp.Tests/HaulContractEnrichmentTests.cs`.
- Modify `NexusApp/Models/Haul.cs` (add `Reward`, `ContractedBy`, `ContractObjectives`).
- Modify `NexusApp/Services/HaulTracker.cs` (add `ApplyContractDetails` + pending stash).

**New (OCR/scanner, Tasks 4-5):**
- `NexusApp/Services/ContractOcrService.cs`, `NexusApp/Services/ContractScanner.cs`.

**Modify (wiring + UI, Tasks 6-8):**
- `NexusApp/Models/AppSettings.cs`, `NexusApp/App.xaml.cs`.
- `NexusApp/Views/OverlayWindow.xaml(.cs)`, `NexusApp/Views/HaulingPage.cs`.

---

### Task 1: Contract model + Haul fields

**Files:**
- Create: `NexusApp/Models/ContractDetails.cs`
- Modify: `NexusApp/Models/Haul.cs`

**Interfaces:**
- Produces: `ContractObjective { string Commodity; int Scu; string Pickup; string Dropoff; }`; `ContractDetails { string Title; int Reward; string ContractedBy; List<ContractObjective> Objectives; }`; new `Haul` members `int Reward`, `string ContractedBy`, `List<ContractObjective> ContractObjectives`.

- [ ] **Step 1: Create `ContractDetails.cs`**

```csharp
namespace NexusApp.Models;

// One Deliver/Collect leg of a contract, read from the in-game Contracts panel (OCR).
public sealed class ContractObjective
{
    public string Commodity { get; init; } = "";
    public int Scu { get; init; }
    public string Pickup { get; init; } = "";    // "Collect X from <pickup>"
    public string Dropoff { get; init; } = "";   // "Deliver N SCU of X to <dropoff>"
}

// A hauling contract's full detail, parsed from the Contracts panel. No player identity.
public sealed class ContractDetails
{
    public string Title { get; init; } = "";
    public int Reward { get; init; }             // aUEC
    public string ContractedBy { get; init; } = "";
    public List<ContractObjective> Objectives { get; init; } = new();
}
```

- [ ] **Step 2: Add fields to `Haul.cs`** (inside the `Haul` class, after `PickupName`):

```csharp
    public int Reward { get; set; }                 // aUEC, from OCR (0 = unknown)
    public string ContractedBy { get; set; } = "";  // from OCR (cleaner than the generator)
    public List<ContractObjective> ContractObjectives { get; set; } = new();  // OCR-sourced
```

- [ ] **Step 3: Compile-verify the test project** - `cd /home/znoland/NexusApp && dotnet test NexusApp.Tests/NexusApp.Tests.csproj` (NOTE: dotnet is unavailable in WSL; this runs on Windows/CI). Proofread by inspection: `ContractObjective` is referenced by `Haul.ContractObjectives` so both must be in `NexusApp.Models`; ImplicitUsings covers `List`.

- [ ] **Step 4: Commit** (ask Zach): `git add NexusApp/Models/ContractDetails.cs NexusApp/Models/Haul.cs && git commit -m "feat(contract): add contract detail model + haul fields"`

---

### Task 2: ContractParser (pure OCR-text parser)

**Files:**
- Create: `NexusApp/Services/ContractParser.cs`
- Test: `NexusApp.Tests/ContractParserTests.cs`

**Interfaces:**
- Consumes: `ContractDetails`/`ContractObjective` (Task 1).
- Produces: `static ContractDetails? ContractParser.Parse(string ocrText)` (null when no contract anchor); `static string ContractParser.NormalizeTitle(string title)`.

- [ ] **Step 1: Write the failing tests** (`ContractParserTests.cs`). The fixtures are the panel text the OCR would yield (line-joined, lightly noisy), built from the three real screenshots:

```csharp
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

public class ContractParserTests
{
    // Covalex single-commodity (screenshot 1).
    private const string Covalex =
        "Opportunity for Independent Cargo Hauler | Patch City > Checkmate [100 Rep]\n" +
        "Reward  119,000\nContract Deadline N/A\nContracted By Covalex Independent Contractors\n" +
        "DETAILS\nEarning such accolades as 'Imperial Finances' Top 10 ...\n" +
        "PRIMARY OBJECTIVES\n" +
        "Deliver 0/6 SCU of Medical Supplies to Checkmate at the L4 Lagrange of Pyro II.\n" +
        "Collect Medical Supplies from Patch City.\n";

    // Ling multi-commodity (screenshot 2).
    private const string Ling =
        "Cargo Hauling Opportunity with Ling Hauling | Chawla's Beach > Rayari Kaltag Research\n" +
        "Reward 169,250\nContract Deadline N/A\nContracted By Ling Family Hauling\n" +
        "PRIMARY OBJECTIVES\n" +
        "Deliver 0/4 SCU of Sunset Berries to Rayari Kaltag Research Outpost on Calliope.\n" +
        "Collect Sunset Berries from Chawla's Beach.\n" +
        "Deliver 0/2 SCU of Golden Medmon to Rayari Kaltag Research Outpost on Calliope.\n" +
        "Collect Golden Medmon from Chawla's Beach.\n";

    // CFP "Need a Hauler" (screenshot 3) - the log can't supply this cargo.
    private const string Cfp =
        "Need a Hauler [50/200 Rep]\nReward 139,250\nContract Deadline N/A\n" +
        "Contracted By Citizens For Prosperity\nDETAILS\nHi, we need to move some cargo ...\n" +
        "PRIMARY OBJECTIVES\n" +
        "Deliver 0/6 SCU of Hydrogen to Ruin Station above Pyro VI.\n" +
        "Collect Hydrogen from Jackson's Swap.\n";

    [Fact]
    public void Parse_Covalex_AllFields()
    {
        var d = ContractParser.Parse(Covalex);
        Assert.NotNull(d);
        Assert.Contains("Patch City > Checkmate", d!.Title);
        Assert.Equal(119000, d.Reward);
        Assert.Equal("Covalex Independent Contractors", d.ContractedBy);
        var o = Assert.Single(d.Objectives);
        Assert.Equal("Medical Supplies", o.Commodity);
        Assert.Equal(6, o.Scu);
        Assert.Equal("Patch City", o.Pickup);
        Assert.StartsWith("Checkmate", o.Dropoff);
    }

    [Fact]
    public void Parse_Ling_TwoObjectives()
    {
        var d = ContractParser.Parse(Ling);
        Assert.Equal(169250, d!.Reward);
        Assert.Equal(2, d.Objectives.Count);
        Assert.Contains(d.Objectives, o => o.Commodity == "Sunset Berries" && o.Scu == 4 && o.Pickup == "Chawla's Beach");
        Assert.Contains(d.Objectives, o => o.Commodity == "Golden Medmon" && o.Scu == 2);
    }

    [Fact]
    public void Parse_Cfp_FillsCargoTheLogLacks()
    {
        var d = ContractParser.Parse(Cfp);
        Assert.Equal("Citizens For Prosperity", d!.ContractedBy);
        var o = Assert.Single(d.Objectives);
        Assert.Equal("Hydrogen", o.Commodity);
        Assert.Equal(6, o.Scu);
        Assert.Equal("Jackson's Swap", o.Pickup);
        Assert.StartsWith("Ruin Station", o.Dropoff);
    }

    [Fact]
    public void Parse_NonContractText_ReturnsNull()
    {
        Assert.Null(ContractParser.Parse("HOME HEALTH COMMS CONTRACTS MAPS\nsome unrelated screen text"));
    }

    [Theory]
    [InlineData("Need a Hauler [50/200 Rep]", "need a hauler")]
    [InlineData("Opportunity for Independent Cargo Hauler | Patch City > Checkmate [100 Rep]",
                "opportunity for independent cargo hauler patch city checkmate")]
    public void NormalizeTitle_StripsRepTagAndPunctuation(string raw, string expected) =>
        Assert.Equal(expected, ContractParser.NormalizeTitle(raw));
}
```

- [ ] **Step 2: Run to verify failure** (Windows/CI): `dotnet test NexusApp.Tests/NexusApp.Tests.csproj --filter ContractParserTests` -> FAIL.

- [ ] **Step 3: Write `ContractParser.cs`**

```csharp
using System.Globalization;
using System.Text.RegularExpressions;
using NexusApp.Models;

namespace NexusApp.Services;

// Pure parsing of the in-game Contracts-panel OCR text. Tolerant of OCR noise; anchored on
// Reward / Contracted By / Deliver / Collect. Returns null when no contract anchor is present
// (which is also how the scanner knows no contract is on screen). No player identity is read.
public static class ContractParser
{
    private static readonly Regex RewardRx = new(@"Reward[^\d]{0,8}([\d][\d,]{2,})", RegexOptions.Compiled);
    private static readonly Regex ContractedByRx = new(@"Contracted By\s+([^\r\n]+)", RegexOptions.Compiled);
    private static readonly Regex DeliverRx = new(
        @"Deliver\s+\d+\s*/\s*(?<scu>\d+)\s+SCU of\s+(?<commodity>.+?)\s+to\s+(?<dropoff>[^.\r\n]+)",
        RegexOptions.Compiled);
    private static readonly Regex CollectRx = new(
        @"Collect\s+(?<commodity>.+?)\s+from\s+(?<pickup>[^.\r\n]+)", RegexOptions.Compiled);
    private static readonly Regex RepTag = new(@"\[[^\]]*\]", RegexOptions.Compiled);

    public static ContractDetails? Parse(string ocrText)
    {
        if (string.IsNullOrWhiteSpace(ocrText)) return null;

        // Pickup sources keyed by commodity (so a Deliver can borrow its Collect's source).
        var pickups = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match c in CollectRx.Matches(ocrText))
            pickups[c.Groups["commodity"].Value.Trim()] = c.Groups["pickup"].Value.Trim();

        var objectives = new List<ContractObjective>();
        foreach (Match m in DeliverRx.Matches(ocrText))
        {
            var commodity = m.Groups["commodity"].Value.Trim();
            objectives.Add(new ContractObjective
            {
                Commodity = commodity,
                Scu = int.TryParse(m.Groups["scu"].Value, out var s) ? s : 0,
                Dropoff = m.Groups["dropoff"].Value.Trim(),
                Pickup = pickups.TryGetValue(commodity, out var p) ? p : "",
            });
        }

        var contractedBy = ContractedByRx.Match(ocrText) is { Success: true } cb
            ? cb.Groups[1].Value.Trim() : "";

        // Not a contract panel unless we found at least the contractor or a Deliver objective.
        if (objectives.Count == 0 && contractedBy.Length == 0) return null;

        int reward = 0;
        if (RewardRx.Match(ocrText) is { Success: true } r &&
            int.TryParse(r.Groups[1].Value.Replace(",", ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out var rv))
            reward = rv;

        return new ContractDetails
        {
            Title = ExtractTitle(ocrText),
            Reward = reward,
            ContractedBy = contractedBy,
            Objectives = objectives,
        };
    }

    // The heading: text before the first known anchor line.
    private static string ExtractTitle(string text)
    {
        foreach (var anchor in new[] { "Reward", "Contract Deadline", "Contracted By", "DETAILS", "PRIMARY OBJECTIVES" })
        {
            var i = text.IndexOf(anchor, StringComparison.Ordinal);
            if (i > 0) return Collapse(text[..i]);
        }
        return Collapse(text);
    }

    public static string NormalizeTitle(string title)
    {
        var noTag = RepTag.Replace(title, " ");
        var lower = noTag.ToLowerInvariant();
        var sb = new System.Text.StringBuilder(lower.Length);
        foreach (var ch in lower)
            sb.Append(char.IsLetterOrDigit(ch) ? ch : (ch == ' ' || ch == '\n' || ch == '\r' || ch == '\t' ? ' ' : ' '));
        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }

    private static string Collapse(string s) => Regex.Replace(s, @"\s+", " ").Trim();
}
```

- [ ] **Step 4: Run to verify pass** (Windows/CI): `dotnet test NexusApp.Tests/NexusApp.Tests.csproj --filter ContractParserTests` -> PASS.

- [ ] **Step 5: Verify by inspection** (no compiler in WSL): trace `DeliverRx` against each fixture (scu/commodity/dropoff stop before the `.`); confirm `CollectRx` keys pickups by commodity so the Deliver borrows the right source; confirm `NonContractText` yields null (no Deliver, no "Contracted By"); trace `NormalizeTitle` for the two `[InlineData]` cases (strip `[..]`, lowercase, non-alphanumerics -> spaces, `\s+` collapse). Note: `>`/`|` and the stripped `[..]` tag all become spaces and the final `\s+` collapse yields single spaces, so the route title normalizes to `opportunity for independent cargo hauler patch city checkmate` (already the test's expected value).

- [ ] **Step 6: Commit** (ask Zach): `git add NexusApp/Services/ContractParser.cs NexusApp.Tests/ContractParserTests.cs && git commit -m "feat(contract): add Contracts-panel OCR text parser with tests"`

---

### Task 3: Haul enrichment by title

**Files:**
- Modify: `NexusApp/Services/HaulTracker.cs`
- Test: `NexusApp.Tests/HaulContractEnrichmentTests.cs`

**Interfaces:**
- Consumes: `ContractParser.NormalizeTitle` (Task 2), `ContractDetails` (Task 1).
- Produces on `HaulTracker`: `void ApplyContractDetails(ContractDetails d)` - matches an active haul by normalized title (`Haul.RouteTitle` vs `d.Title`) and sets `Reward`/`ContractedBy`/`ContractObjectives`; if no match, stashes `d` and applies it when a haul with that title is later created.

- [ ] **Step 1: Write the failing tests** (`HaulContractEnrichmentTests.cs`):

```csharp
using NexusApp.Models;
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

public class HaulContractEnrichmentTests
{
    private static GameLogEntry E(string raw) => new() { Raw = raw, Category = LogCategory.Other };

    private static ContractDetails Cfp() => new()
    {
        Title = "Need a Hauler [50/200 Rep]",
        Reward = 139250,
        ContractedBy = "Citizens For Prosperity",
        Objectives = { new ContractObjective { Commodity = "Hydrogen", Scu = 6, Pickup = "Jackson's Swap", Dropoff = "Ruin Station" } },
    };

    // The log haul whose RouteTitle is "Need a Hauler" comes from this Contract Accepted line.
    private const string CfpAccept =
        "<2026-06-27T21:49:56.564Z> [Notice] <SHUDEvent_OnNotification> Added notification " +
        "\"Contract Accepted:  Need a Hauler <EM4>[50/200 Rep]</EM4>: \" [8] to queue. New queue size: 5, " +
        "MissionId: [6b4e3396-1506-4051-b348-79c4eabae9d9], ObjectiveId: [] [x]";

    [Fact]
    public void Enrich_MatchesActiveHaulByTitle()
    {
        var t = new HaulTracker();
        t.Ingest(E(HaulLogParserFixtures.CfpMarkerPickup));   // creates the haul (mission 6b4e3396)
        t.Ingest(E(CfpAccept));                               // sets RouteTitle "Need a Hauler"
        t.ApplyContractDetails(Cfp());
        var h = Assert.Single(t.AllHauls);
        Assert.Equal(139250, h.Reward);
        Assert.Equal("Citizens For Prosperity", h.ContractedBy);
        Assert.Equal("Hydrogen", Assert.Single(h.ContractObjectives).Commodity);
    }

    [Fact]
    public void Enrich_BeforeHaulExists_AppliesWhenItAppears()
    {
        var t = new HaulTracker();
        t.ApplyContractDetails(Cfp());                        // no haul yet -> stashed
        t.Ingest(E(HaulLogParserFixtures.CfpMarkerPickup));
        t.Ingest(E(CfpAccept));                               // RouteTitle set -> pending applied
        Assert.Equal(139250, Assert.Single(t.AllHauls).Reward);
    }
}
```

- [ ] **Step 2: Run to verify failure** (Windows/CI).

- [ ] **Step 3: Implement on `HaulTracker`.** Add a pending stash field, the `ApplyContractDetails` method, and a one-line hook in the accept/marker paths so a stashed contract applies when a haul's title becomes known. Concretely:

Add field near `_currentShardId`:
```csharp
    private readonly Dictionary<string, ContractDetails> _pendingContracts = new();   // by normalized title
```

Add methods:
```csharp
    /// <summary>Attach OCR'd contract detail to the active haul whose title matches; stash if none yet.</summary>
    public void ApplyContractDetails(ContractDetails d)
    {
        var key = ContractParser.NormalizeTitle(d.Title);
        var h = _order.Find(x => x.IsActive && ContractParser.NormalizeTitle(x.RouteTitle) == key && key.Length > 0);
        if (h is null) { _pendingContracts[key] = d; return; }
        Enrich(h, d);
        Logger.Info($"[CONTRACT] enriched {h.Company} reward {d.Reward}");
        Changed?.Invoke();
    }

    private static void Enrich(Haul h, ContractDetails d)
    {
        h.Reward = d.Reward;
        h.ContractedBy = d.ContractedBy;
        h.ContractObjectives = d.Objectives;
    }

    private void TryApplyPending(Haul h)
    {
        if (_pendingContracts.Count == 0 || string.IsNullOrEmpty(h.RouteTitle)) return;
        var key = ContractParser.NormalizeTitle(h.RouteTitle);
        if (_pendingContracts.TryGetValue(key, out var d)) { Enrich(h, d); _pendingContracts.Remove(key); }
    }
```

In the existing `Ingest` accept branch (where `ah.RouteTitle = accept.Title;` is set), add `TryApplyPending(ah);` right after setting the title (so a stashed contract applies once the title is known). Also call `_pendingContracts.Clear()` inside `ClearInternal()`.

- [ ] **Step 4: Run to verify pass** (Windows/CI).

- [ ] **Step 5: Verify by inspection:** the match uses `Haul.RouteTitle` (set by the Contract Accepted line) normalized against `d.Title`; both "Need a Hauler [50/200 Rep]" (OCR) and "Need a Hauler" (log, EM-stripped) normalize to "need a hauler". Confirm `TryApplyPending` is called after the accept sets RouteTitle, and `ClearInternal` clears the stash.

- [ ] **Step 6: Commit** (ask Zach): `git add NexusApp/Services/HaulTracker.cs NexusApp.Tests/HaulContractEnrichmentTests.cs && git commit -m "feat(contract): enrich hauls from OCR contract details by title"`

---

### Task 4: ContractOcrService

**Files:**
- Create: `NexusApp/Services/ContractOcrService.cs`

**Interfaces:**
- Produces: `class ContractOcrService : IDisposable` with `bool IsAvailable`, `void SetRegion(int x,int y,int w,int h)`, `Task<string?> ScanRegionTextAsync()` (captures the set region, preprocesses with a lighter text-panel pass, `RecognizeAsync`, returns full `OcrResult.Text` or null).

- [ ] **Step 1: Read** `NexusApp/Services/OcrService.cs` for the engine init (lines 26-35), the GDI `CaptureRegion` P/Invoke + struct + DllImports (lines 176-198, 242-266), and `ToSoftwareBitmap` (200-205). You will MIRROR (copy) these into the new service - do NOT modify `OcrService`.

- [ ] **Step 2: Create `ContractOcrService.cs`.** Self-contained: its own `OcrEngine`, its own copy of the GDI capture P/Invoke and `ToSoftwareBitmap`, and a NEW lighter preprocessing (invert + the same x1.4 contrast as the RS path, but `scale = 2` not 6, since panel text is already large). API:

```csharp
public sealed class ContractOcrService : IDisposable
{
    private OcrEngine? _engine;
    public bool IsAvailable { get; }
    private int _x, _y, _w, _h; private bool _hasRegion;
    public void SetRegion(int x, int y, int w, int h) { _x=x; _y=y; _w=w; _h=h; _hasRegion = w>0 && h>0; }

    public ContractOcrService() { /* same TryCreateFromUserProfileLanguages ?? en-US as OcrService */ }

    public async Task<string?> ScanRegionTextAsync()
    {
        if (!IsAvailable || _engine is null || !_hasRegion) return null;
        var raw = CaptureRegion(_x, _y, _w, _h);
        if (raw is null) return null;
        var processed = Preprocess(raw, _w, _h, out int pw, out int ph);   // scale=2 invert+contrast
        var bmp = ToSoftwareBitmap(processed, pw, ph);
        var result = await _engine.RecognizeAsync(bmp);
        return result.Text;
    }
    // + copied CaptureRegion/ToSoftwareBitmap/DllImports/BITMAPINFOHEADER, new Preprocess(scale=2), Dispose.
}
```

Write the full file with the copied capture/convert/P-Invoke verbatim from `OcrService` and the `scale=2` `Preprocess`. Comment that this duplication exists to keep the locked RS service untouched.

- [ ] **Step 3: Verify by inspection** (no compiler): the WinRT usings (`Windows.Graphics.Imaging`, `Windows.Media.Ocr`, `Windows.Globalization`, `System.Runtime.InteropServices.WindowsRuntime` for `AsBuffer`) match `OcrService`'s; the capture P/Invoke is byte-identical; `Preprocess` differs only in `scale`. Note: NOT buildable in WSL (WPF/WinRT) - CI/Windows is the gate.

- [ ] **Step 4: Commit** (ask Zach): `git add NexusApp/Services/ContractOcrService.cs && git commit -m "feat(contract): add ContractOcrService (text-panel OCR, RS path untouched)"`

---

### Task 5: ContractScanner

**Files:**
- Create: `NexusApp/Services/ContractScanner.cs`

**Interfaces:**
- Consumes: `ContractOcrService` (Task 4), `ContractParser` (Task 2), `ContractDetails` (Task 1).
- Produces: `class ContractScanner : IDisposable` with `void Start()`, `void Stop()`, `bool IsRunning`, `event Action<ContractDetails>? ContractScanned`. Polls ~1s; dedupes by normalized title.

- [ ] **Step 1: Read** `NexusApp/Services/ScannerService.cs` for the `System.Timers.Timer` start/stop/rearm pattern (AutoReset=false, recreate per tick) and its `[SCAN]` logging cadence.

- [ ] **Step 2: Create `ContractScanner.cs`** mirroring `ScannerService`'s timer pattern at a 1000ms interval. Each tick: `var text = await _ocr.ScanRegionTextAsync(); var d = text is null ? null : ContractParser.Parse(text);` if `d` is non-null and `ContractParser.NormalizeTitle(d.Title)` differs from the last imported key, set the key, `Logger.Info($"[CONTRACT] scanned: {d.ContractedBy} reward {d.Reward}");` and raise `ContractScanned?.Invoke(d)`. Constructor takes a `ContractOcrService`. `Start()`/`Stop()` toggle the timer; guard against overlapping async ticks with a `_busy` bool.

- [ ] **Step 3: Verify by inspection:** the timer rearm matches `ScannerService`; the dedup key is the normalized title; no per-tick work when `ScanRegionTextAsync` returns null (no region / engine unavailable / no contract). NOT buildable in WSL.

- [ ] **Step 4: Commit** (ask Zach): `git add NexusApp/Services/ContractScanner.cs && git commit -m "feat(contract): add ContractScanner poll + dedup"`

---

### Task 6: Settings + App wiring

**Files:**
- Modify: `NexusApp/Models/AppSettings.cs`, `NexusApp/App.xaml.cs`

**Interfaces:**
- Produces: `App.ContractOcr` (ContractOcrService), `App.ContractScan` (ContractScanner); `AppSettings.ContractRegion` (`ScanRegion?`), `AppSettings.AutoScanContracts` (bool).

- [ ] **Step 1:** Add to `AppSettings.cs`: `public ScanRegion? ContractRegion { get; set; }` and `public bool AutoScanContracts { get; set; }` (additive, no schema bump - mirror the existing `ScanRegion`/bool fields).
- [ ] **Step 2: Read** the `Hauls`/`Shards` static-property + construction + dispose blocks in `App.xaml.cs`. Add `public static ContractOcrService ContractOcr` and `public static ContractScanner ContractScan` statics.
- [ ] **Step 3:** In `OnStartup` after the `Shards` block: construct `ContractOcr = new ContractOcrService();` apply the saved region if present (`if (Settings.Current.ContractRegion is {} r) ContractOcr.SetRegion(r.X, r.Y, r.Width, r.Height);`); `ContractScan = new ContractScanner(ContractOcr);` wire the scanned event MARSHALED to the UI thread (the scanner runs on a `System.Timers.Timer` thread, and `ApplyContractDetails` raises `Changed` which the overlay handles by touching WPF directly): `ContractScan.ContractScanned += d => Current.Dispatcher.Invoke(() => Hauls.ApplyContractDetails(d));` and `if (Settings.Current.AutoScanContracts) ContractScan.Start();`.
- [ ] **Step 4:** Dispose both in `OnExit` (alongside `Hauls?.Dispose()`).
- [ ] **Step 5: Verify by inspection:** `Settings.Current.ContractRegion`/`AutoScanContracts` round-trip via STJ; `ScanRegion` is the existing `{X,Y,Width,Height}` type; construction order is after services exist. NOT buildable in WSL.
- [ ] **Step 6: Commit** (ask Zach): `git add NexusApp/Models/AppSettings.cs NexusApp/App.xaml.cs && git commit -m "feat(contract): persist region + wire ContractScanner into app"`

---

### Task 7: Overlay controls (Set region + auto-scan toggle)

**Files:**
- Modify: `NexusApp/Views/OverlayWindow.xaml(.cs)`

**Interfaces:**
- Consumes: `App.ContractOcr`, `App.ContractScan`, `RegionSelectorWindow`.

- [ ] **Step 1: Read** how the RS scan does it in `OverlayWindow.xaml.cs`: `SetRegion_Click` -> `RegionSelectorWindow` -> `ScanRegionSelected` (and how the result is saved to `AppSettings.ScanRegion`), and `BuildScanControls`/`SwitchPair` for the on/off toggle idiom.
- [ ] **Step 2:** Add to the HAULING tab (or the scan controls area) a "Set contract region" button: open `RegionSelectorWindow` via the same path the RS region uses; on `RegionSelected`, save `Settings.Current.ContractRegion = new ScanRegion {X=..,Y=..,Width=..,Height=..}; Settings.Save();` and `App.ContractOcr.SetRegion(...)`.
- [ ] **Step 3:** Add an "Auto-scan contracts" toggle mirroring the existing `SwitchPair` toggles: on -> `App.ContractScan.Start(); Settings.Current.AutoScanContracts = true; Settings.Save();`; off -> `Stop()` + persist false. Reflect initial state from `App.ContractScan.IsRunning`.
- [ ] **Step 4: Verify by inspection:** reuses `RegionSelectorWindow` exactly as the RS region (incl. the multi-monitor `ShowOnMonitorOf` path); no emoji/em-dash; toggle persists. NOT buildable in WSL.
- [ ] **Step 5: Commit** (ask Zach): `git add NexusApp/Views/OverlayWindow.xaml NexusApp/Views/OverlayWindow.xaml.cs && git commit -m "feat(contract): overlay set-region + auto-scan toggle"`

---

### Task 8: Display enriched details

**Files:**
- Modify: `NexusApp/Views/HaulingPage.cs`, `NexusApp/Views/OverlayWindow.xaml.cs` (RebuildHaulingPanel)

**Interfaces:**
- Consumes: `Haul.Reward`, `Haul.ContractedBy`, `Haul.ContractObjectives`.

- [ ] **Step 1:** In `HaulingPage.ActiveHaulCard` (and `FinishedRow`): when `h.Reward > 0` show a reward line (e.g. `$"{h.Reward:N0} aUEC"` in AccentBrush); when `h.ContractedBy` is non-empty prefer it for the company line. When `h.ContractObjectives.Count > 0`, render those (`$"{role}: {o.Scu} SCU {o.Commodity}  {o.Pickup} -> {o.Dropoff}"`) instead of the log legs; else keep the existing leg rows.
- [ ] **Step 2:** In overlay `RebuildHaulingPanel`: same - show reward + contractor when present and the OCR objectives when present.
- [ ] **Step 3: Verify by inspection:** only existing brush keys; `:N0` formats the int; no emoji/em-dash; falls back to log legs when no OCR objectives. NOT buildable in WSL.
- [ ] **Step 4: Commit** (ask Zach): `git add NexusApp/Views/HaulingPage.cs NexusApp/Views/OverlayWindow.xaml.cs && git commit -m "feat(contract): show reward + contractor + OCR objectives on hauls"`

---

## Final verification (Windows/CI - needs Zach's yes for merge)
- `~/test_nexus.ps1`: build + `dotnet test` (new ContractParser + enrichment tests green).
- Runtime: draw the contract region over the Contracts detail panel, toggle Auto-scan contracts on, browse Accepted; confirm each haul gains reward + contractor + commodity/SCU/pickup/dropoff (esp. "Need a Hauler"), `[CONTRACT]` lines in the App Log Monitor, no handle anywhere, and the RS mining scan still works unchanged.

## Known v1 limits (from the spec)
- Per-objective "[done]" matching between OCR objectives and log legs is deferred (overall log status shown instead).
- Single-contract-at-a-time via the detail panel (no list OCR).
- Title-based matching is best-effort; OCR slips in the title can miss a match (the contract stays in the pending stash).
