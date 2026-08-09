using System.Globalization;
using NexusApp.Models;
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

public class AutoLoadTrackerTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"al_trk_{Guid.NewGuid():N}.json");
    private readonly string _activePath = Path.Combine(Path.GetTempPath(), $"al_active_{Guid.NewGuid():N}.json");
    private DateTime _now = new(2026, 8, 8, 12, 0, 0, DateTimeKind.Utc);
    private readonly AutoLoadSampleStore _store;
    private readonly AutoLoadTracker _tracker;

    public AutoLoadTrackerTests()
    {
        _store = new AutoLoadSampleStore(_path);
        _tracker = new AutoLoadTracker(new ProfitTracker(historyPath: Path.Combine(Path.GetTempPath(),
            $"al_hist_{Guid.NewGuid():N}.json")), _store, () => _now, activePath: _activePath);
    }
    public void Dispose()
    {
        _tracker.Dispose();
        try { File.Delete(_path); } catch { }
        try { File.Delete(_activePath); } catch { }
    }

    // A second tracker "restarting" against the same _store/_activePath as _tracker, its own
    // fresh ProfitTracker/history file (mirrors the primary tracker's own construction idiom)
    // since only the shared _store and _activePath matter for the restore contract under test.
    private AutoLoadTracker NewTrackerOnSamePaths()
        => new(new ProfitTracker(historyPath: Path.Combine(Path.GetTempPath(),
            $"al_hist_{Guid.NewGuid():N}.json")), _store, () => _now, activePath: _activePath);

    private CommodityTransaction Tx(bool auto, TransactionKind kind = TransactionKind.Buy, int ageSeconds = 5)
        => new()
        {
            TimestampUtc = _now.AddSeconds(-ageSeconds), Kind = kind, Amount = 1_125_896,
            Scu = 1440m, ShopName = "SCShop_Admin_lt_base_g", AutoLoading = auto,
            Boxes = new[] { new CargoBoxGroup(24m, 60) },
        };

    [Fact]
    public void Apply_FlaggedFreshBuy_OpensLoadEntryWithPrediction()
    {
        _tracker.Apply(Tx(auto: true));
        var e = Assert.Single(_tracker.Entries);
        Assert.Equal(TransactionKind.Buy, e.Kind);
        Assert.Equal(612, e.PredictedSeconds);
    }

    [Fact]
    public void Apply_UnflaggedOrStale_Ignored()
    {
        _tracker.Apply(Tx(auto: false));
        _tracker.Apply(Tx(auto: true, ageSeconds: 120));   // beyond FreshWindow: startup replay
        Assert.Empty(_tracker.Entries);
    }

    [Fact]
    public void Apply_FlaggedSell_OpensUnloadEntry()
    {
        _tracker.Apply(Tx(auto: true, kind: TransactionKind.Sell));
        Assert.Equal(TransactionKind.Sell, Assert.Single(_tracker.Entries).Kind);
    }

    [Fact]
    public void Complete_WritesSampleAndRemoves()
    {
        _tracker.Apply(Tx(auto: true));
        _now = _now.AddMinutes(27);
        _tracker.Complete(_tracker.Entries[0]);
        Assert.Empty(_tracker.Entries);
        var s = Assert.Single(_store.ReadAll());
        Assert.Equal(27 * 60 + 5, s.ElapsedSeconds);
        Assert.Equal(612, s.PredictedSeconds);
        Assert.False(s.Abandoned);
        Assert.Equal(60, s.Boxes["24"]);
    }

    [Fact]
    public void Discard_RemovesWithoutSample()
    {
        _tracker.Apply(Tx(auto: true));
        _tracker.Discard(_tracker.Entries[0]);
        Assert.Empty(_tracker.Entries);
        Assert.Empty(_store.ReadAll());
    }

    [Fact]
    public void ExpireStale_AbandonsAfterTwoHours()
    {
        _tracker.Apply(Tx(auto: true));
        _now = _now.AddHours(3);
        _tracker.ExpireStale();
        Assert.Empty(_tracker.Entries);
        Assert.True(Assert.Single(_store.ReadAll()).Abandoned);
    }

    [Fact]
    public void Reset_ClearsWithoutSamples()
    {
        _tracker.Apply(Tx(auto: true));
        _tracker.Reset();
        Assert.Empty(_tracker.Entries);
        Assert.Empty(_store.ReadAll());
    }

    [Fact]
    public void ConcurrentEntries_BothHeld()
    {
        _tracker.Apply(Tx(auto: true));
        _tracker.Apply(Tx(auto: true, kind: TransactionKind.Sell));
        Assert.Equal(2, _tracker.Entries.Count);
    }

    [Fact]
    public void EntriesChanged_FiresOnEveryMutation()
    {
        var fired = 0;
        _tracker.EntriesChanged += () => fired++;
        _tracker.Apply(Tx(auto: true));
        _tracker.Discard(_tracker.Entries[0]);
        Assert.Equal(2, fired);
    }

    // End-to-end wiring check: a real Game.log buy line goes through ProfitTracker.Ingest, which
    // raises TransactionParsed, which this tracker (built with the DEFAULT clock, no fake _now)
    // consumes via its own Apply subscription. Every other test in this file calls Apply directly
    // and never exercises that event path. The fixture line's own stamp is replaced with a live
    // DateTime.UtcNow stamp so it lands inside FreshWindow against the tracker's real clock.
    [Fact]
    public void ProfitTrackerIngest_RealBuyLine_OpensEntryViaEventPath()
    {
        var histPath = Path.Combine(Path.GetTempPath(), $"al_hist2_{Guid.NewGuid():N}.json");
        var activePath2 = Path.Combine(Path.GetTempPath(), $"al_active2_{Guid.NewGuid():N}.json");
        using var profit = new ProfitTracker(historyPath: histPath);
        using var tracker2 = new AutoLoadTracker(profit, _store, activePath: activePath2);

        var stamp = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        var raw = CommodityLogFixtures.BuyLine.Replace("2026-07-04T13:35:36.565Z", stamp);

        profit.Ingest(new GameLogEntry { Raw = raw, Category = LogCategory.Other });

        var e = Assert.Single(tracker2.Entries);
        Assert.Equal(TransactionKind.Buy, e.Kind);
        Assert.Equal(128m, e.Scu);   // 12800 cSCU / 100
        var box = Assert.Single(e.Boxes);
        Assert.Equal(32m, box.BoxSize);
        Assert.Equal(4, box.UnitAmount);

        try { File.Delete(histPath); } catch { }
        try { File.Delete(activePath2); } catch { }
    }

    // Owner-reversed trade-off: active timer entries must survive an app restart. "Restart" here
    // is a second AutoLoadTracker built on the same _store/_activePath - exactly what App.xaml.cs
    // does across a real relaunch, just without the process boundary.
    [Fact]
    public void Restart_RestoresActiveEntries()
    {
        _tracker.Apply(Tx(auto: true));
        var original = Assert.Single(_tracker.Entries);

        using var trackerB = NewTrackerOnSamePaths();

        var restored = Assert.Single(trackerB.Entries);
        Assert.Equal(original.StartUtc, restored.StartUtc);
        Assert.Equal(original.PredictedSeconds, restored.PredictedSeconds);
    }

    [Fact]
    public void Restart_AbandonsExpiredPersistedEntries()
    {
        _tracker.Apply(Tx(auto: true));
        _now = _now.AddHours(3);   // advance past AbandonAfter before the "restart" reads the file

        using var trackerB = NewTrackerOnSamePaths();

        Assert.Empty(trackerB.Entries);
        Assert.True(Assert.Single(_store.ReadAll()).Abandoned);
    }

    [Fact]
    public void Apply_DuplicateOfRestoredEntry_Ignored()
    {
        var tx = Tx(auto: true);
        _tracker.Apply(tx);

        using var trackerB = NewTrackerOnSamePaths();
        Assert.Single(trackerB.Entries);

        trackerB.Apply(tx);   // same StartUtc/Kind/ShopName as the entry restored into trackerB
        Assert.Single(trackerB.Entries);
    }

    [Fact]
    public void Reset_ClearsThePersistedFile()
    {
        _tracker.Apply(Tx(auto: true));
        _tracker.Reset();

        using var trackerB = NewTrackerOnSamePaths();
        Assert.Empty(trackerB.Entries);
    }
}
