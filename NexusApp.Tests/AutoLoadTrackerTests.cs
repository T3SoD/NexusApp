using NexusApp.Models;
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

public class AutoLoadTrackerTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"al_trk_{Guid.NewGuid():N}.json");
    private DateTime _now = new(2026, 8, 8, 12, 0, 0, DateTimeKind.Utc);
    private readonly AutoLoadSampleStore _store;
    private readonly AutoLoadTracker _tracker;

    public AutoLoadTrackerTests()
    {
        _store = new AutoLoadSampleStore(_path);
        _tracker = new AutoLoadTracker(new ProfitTracker(historyPath: Path.Combine(Path.GetTempPath(),
            $"al_hist_{Guid.NewGuid():N}.json")), _store, () => _now);
    }
    public void Dispose() { _tracker.Dispose(); try { File.Delete(_path); } catch { } }

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
        Assert.Equal(1020, e.PredictedSeconds);
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
        Assert.Equal(1020, s.PredictedSeconds);
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
}
