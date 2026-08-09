using NexusApp.Models;

namespace NexusApp.Services;

// Auto-load timing state. Passive: ProfitTracker pushes parsed transactions in, UI surfaces pull
// entries and tick their own clocks. Game.log never reports completion, so entries end by the
// LOADED click (sample), DISCARD (no sample), the 2h abandon expiry (flagged sample), or a log
// reset (no sample). FreshWindow keeps startup replay from resurrecting old transactions.
public sealed class AutoLoadTracker : IDisposable
{
    public static readonly TimeSpan FreshWindow = TimeSpan.FromSeconds(90);
    public static readonly TimeSpan AbandonAfter = TimeSpan.FromHours(2);

    private readonly ProfitTracker _profit;
    private readonly AutoLoadSampleStore _store;
    private readonly Func<DateTime> _utcNow;
    private readonly AutoLoadTimeTable _table;
    private readonly List<AutoLoadEntry> _entries = new();

    public IReadOnlyList<AutoLoadEntry> Entries { get { lock (_entries) return _entries.ToList(); } }
    public event Action? EntriesChanged;

    public AutoLoadTracker(ProfitTracker profit, AutoLoadSampleStore? store = null,
                           Func<DateTime>? utcNow = null, AutoLoadTimeTable? table = null)
    {
        _profit = profit;
        _store = store ?? new AutoLoadSampleStore();
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _table = table ?? AutoLoadTimeTable.Instance;
        _profit.TransactionParsed += Apply;
        _profit.LogWasReset += Reset;
    }

    public void Apply(CommodityTransaction tx)
    {
        if (!tx.AutoLoading) return;
        if (_utcNow() - tx.TimestampUtc > FreshWindow) return;
        var commodityName = CommodityNameCatalog.Instance.Resolve(tx.ResourceGuid);
        var entry = new AutoLoadEntry(tx.TimestampUtc, tx.Kind, tx.ShopName, commodityName,
            tx.PlaceLabel, tx.PlaceIsArea, tx.Scu, tx.Boxes,
            AutoLoadEstimator.PredictSeconds(_table, tx.Kind, tx.Boxes));
        lock (_entries) _entries.Add(entry);
        Logger.Info($"[CARGO] auto-{Word(entry)} started: {entry.Scu:0.##} SCU {commodityName ?? tx.ResourceGuid} at {entry.ShopName}, predicted {entry.PredictedSeconds?.ToString() ?? "n/a"}s");
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
        EntriesChanged?.Invoke();
    }

    private static string Word(AutoLoadEntry e) => e.Kind == TransactionKind.Buy ? "load" : "unload";

    public void Dispose()
    {
        _profit.TransactionParsed -= Apply;
        _profit.LogWasReset -= Reset;
    }
}
