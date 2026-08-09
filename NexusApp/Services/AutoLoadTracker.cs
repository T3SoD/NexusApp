using System.IO;
using System.Text.Json;
using NexusApp.Models;

namespace NexusApp.Services;

// Auto-load timing state. Passive: ProfitTracker pushes parsed transactions in, UI surfaces pull
// entries and tick their own clocks. Game.log never reports completion, so entries end by the
// LOADED click (sample), DISCARD (no sample), the 2h abandon expiry (flagged sample), or a log
// reset (no sample). FreshWindow keeps startup replay from resurrecting old transactions.
//
// Active entries survive an app restart (owner-reversed trade-off): every mutation persists the
// current entry list to _activePath, and construction restores it back, abandoning anything
// already past AbandonAfter instead of resurrecting a dead clock.
public sealed class AutoLoadTracker : IDisposable
{
    public static readonly TimeSpan FreshWindow = TimeSpan.FromSeconds(90);
    public static readonly TimeSpan AbandonAfter = TimeSpan.FromHours(2);

    private readonly ProfitTracker _profit;
    private readonly AutoLoadSampleStore _store;
    private readonly Func<DateTime> _utcNow;
    private readonly AutoLoadTimeTable _table;
    private readonly string _activePath;
    private readonly List<AutoLoadEntry> _entries = new();

    public IReadOnlyList<AutoLoadEntry> Entries { get { lock (_entries) return _entries.ToList(); } }
    public event Action? EntriesChanged;

    public AutoLoadTracker(ProfitTracker profit, AutoLoadSampleStore? store = null,
                           Func<DateTime>? utcNow = null, AutoLoadTimeTable? table = null,
                           string? activePath = null)
    {
        _profit = profit;
        _store = store ?? new AutoLoadSampleStore();
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _table = table ?? AutoLoadTimeTable.Instance;
        _activePath = activePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "NexusApp", "autoload_active.json");
        _profit.TransactionParsed += Apply;
        _profit.LogWasReset += Reset;

        RestoreActiveEntries();
    }

    public void Apply(CommodityTransaction tx)
    {
        if (!tx.AutoLoading) return;
        if (_utcNow() - tx.TimestampUtc > FreshWindow) return;
        var commodityName = CommodityNameCatalog.Instance.Resolve(tx.ResourceGuid);
        var entry = new AutoLoadEntry(tx.TimestampUtc, tx.Kind, tx.ShopName, commodityName,
            tx.PlaceLabel, tx.PlaceIsArea, tx.Scu, tx.Boxes,
            AutoLoadEstimator.PredictSeconds(_table, tx.Kind, tx.Boxes));
        lock (_entries)
        {
            // Dedupe guard: the same kiosk line can arrive twice - a restored active entry (added
            // silently at construction, see RestoreActiveEntries) meeting its own Game.log replay,
            // or two overlapping tail reads racing a restart. StartUtc+Kind+ShopName is the same
            // settlement identity CommodityTransaction.Key uses, just without the amount/resource
            // components a single kiosk template never repeats within one open timer.
            if (_entries.Any(e => e.StartUtc == entry.StartUtc && e.Kind == entry.Kind && e.ShopName == entry.ShopName))
                return;
            _entries.Add(entry);
        }
        Logger.Info($"[CARGO] auto-{Word(entry)} started: {entry.Scu:0.##} SCU {commodityName ?? tx.ResourceGuid} at {entry.ShopName}, predicted {entry.PredictedSeconds?.ToString() ?? "n/a"}s");
        PersistUnlocked();
        EntriesChanged?.Invoke();
    }

    public void Complete(AutoLoadEntry e) => Close(e, abandoned: false, sample: true);
    public void Discard(AutoLoadEntry e) => Close(e, abandoned: false, sample: false);

    public void ExpireStale()
    {
        foreach (var e in Entries)
            if (_utcNow() - e.StartUtc >= AbandonAfter)
                Close(e, abandoned: true, sample: true);
    }

    public void Reset()
    {
        lock (_entries)
        {
            if (_entries.Count == 0) return;
            _entries.Clear();
        }
        PersistUnlocked();   // rewrites _activePath as an empty list, clearing the persisted file too
        Logger.Info("[CARGO] auto-load entries cleared by log reset");
        EntriesChanged?.Invoke();
    }

    private void Close(AutoLoadEntry e, bool abandoned, bool sample)
    {
        lock (_entries) if (!_entries.Remove(e)) return;
        var elapsed = (int)(_utcNow() - e.StartUtc).TotalSeconds;
        if (sample)
            _store.Append(new AutoLoadSample(1, _utcNow(), e.Kind.ToString(), e.ShopName, e.Scu,
                e.Boxes.ToDictionary(b => b.BoxSize.ToString("0.##"), b => b.UnitAmount),
                elapsed, e.PredictedSeconds, abandoned, _table.GameBuild));
        Logger.Info($"[CARGO] auto-{Word(e)} {(abandoned ? "abandoned" : sample ? "completed" : "discarded")}: elapsed {elapsed}s, predicted {e.PredictedSeconds?.ToString() ?? "n/a"}s");
        PersistUnlocked();
        EntriesChanged?.Invoke();
    }

    // Runs once at construction, AFTER the event subscriptions above so a restored entry is
    // already visible to anything that wires up EntriesChanged right after building the tracker.
    // A restored entry already past AbandonAfter is settled as an abandoned sample immediately
    // instead of re-added - the same outcome ExpireStale would reach on its own next tick, just
    // resolved up front rather than ticking a clock for an entry that can never be completed.
    private void RestoreActiveEntries()
    {
        var restored = ReadActiveUnlocked();
        if (restored.Count == 0) return;

        var kept = new List<AutoLoadEntry>();
        foreach (var e in restored)
        {
            var now = _utcNow();
            if (now - e.StartUtc >= AbandonAfter)
            {
                var elapsed = (int)(now - e.StartUtc).TotalSeconds;
                _store.Append(new AutoLoadSample(1, now, e.Kind.ToString(), e.ShopName, e.Scu,
                    e.Boxes.ToDictionary(b => b.BoxSize.ToString("0.##"), b => b.UnitAmount),
                    elapsed, e.PredictedSeconds, Abandoned: true, _table.GameBuild));
            }
            else
            {
                kept.Add(e);
            }
        }

        if (kept.Count > 0)
        {
            lock (_entries) _entries.AddRange(kept);
            // One summary line, never a [CARGO] started line per entry - those are reserved for
            // freshly-observed kiosk transactions, and a restored entry was already logged once
            // in the session that opened it.
            Logger.Info($"[CARGO] auto-load restored {kept.Count} active entr{(kept.Count == 1 ? "y" : "ies")}");
        }

        PersistUnlocked();   // drops any entries abandoned-on-restore from the on-disk file
        if (kept.Count > 0) EntriesChanged?.Invoke();
    }

    private List<AutoLoadEntry> ReadActiveUnlocked()
    {
        try
        {
            if (!File.Exists(_activePath)) return new();
            var dtos = JsonSerializer.Deserialize<List<ActiveEntryDto>>(File.ReadAllText(_activePath));
            return dtos?.Select(d => d.ToEntry()).ToList() ?? new();
        }
        catch (Exception ex)
        {
            Logger.Info($"[CARGO] auto-load active read failed: {ex.Message}");
            return new();
        }
    }

    // Mirrors AutoLoadSampleStore's own write idiom (create directory, WriteAllText, swallow +
    // log). Called after every mutation - Apply, Close (Complete/Discard/ExpireStale), Reset, and
    // the restore pass itself - so a killed process never loses more than its last write. Takes
    // its own brief lock on _entries to snapshot; "Unlocked" names that this method does not
    // assume the caller is still holding that lock, not that it skips locking altogether.
    private void PersistUnlocked()
    {
        try
        {
            List<AutoLoadEntry> snapshot;
            lock (_entries) snapshot = _entries.ToList();
            Directory.CreateDirectory(Path.GetDirectoryName(_activePath)!);
            File.WriteAllText(_activePath, JsonSerializer.Serialize(
                snapshot.Select(ActiveEntryDto.From).ToList(),
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Logger.Info($"[CARGO] auto-load active write failed: {ex.Message}");
        }
    }

    private static string Word(AutoLoadEntry e) => e.Kind == TransactionKind.Buy ? "load" : "unload";

    public void Dispose()
    {
        _profit.TransactionParsed -= Apply;
        _profit.LogWasReset -= Reset;
    }

    // Private JSON shape for the active-entries file, the same house idiom as AutoLoadTimeTable's
    // and CommodityNameCatalog's own RawFile: a private class with settable properties and the
    // implicit public parameterless constructor, not a positional record. AutoLoadEntry is a
    // record whose only constructor takes an interface-typed Boxes parameter
    // (IReadOnlyList<CargoBoxGroup>), and deserializing straight into a record's parameterized
    // constructor is unproven in this codebase for an interface-typed collection argument; the
    // RawFile shape sidesteps that entirely (mutable List<CargoBoxGroup> property, no constructor
    // matching needed) rather than trusting an unproven combination. CargoBoxGroup itself is a
    // public record with a public constructor, the same shape AutoLoadSample already round-trips.
    private sealed class ActiveEntryDto
    {
        public DateTime StartUtc { get; set; }
        public TransactionKind Kind { get; set; }
        public string ShopName { get; set; } = "";
        public string? CommodityName { get; set; }
        public string? PlaceLabel { get; set; }
        public bool PlaceIsArea { get; set; }
        public decimal Scu { get; set; }
        public List<CargoBoxGroup> Boxes { get; set; } = new();
        public int? PredictedSeconds { get; set; }

        public static ActiveEntryDto From(AutoLoadEntry e) => new()
        {
            StartUtc = e.StartUtc, Kind = e.Kind, ShopName = e.ShopName, CommodityName = e.CommodityName,
            PlaceLabel = e.PlaceLabel, PlaceIsArea = e.PlaceIsArea, Scu = e.Scu,
            Boxes = e.Boxes.ToList(), PredictedSeconds = e.PredictedSeconds,
        };

        public AutoLoadEntry ToEntry() => new(StartUtc, Kind, ShopName, CommodityName, PlaceLabel,
            PlaceIsArea, Scu, Boxes, PredictedSeconds);
    }
}
