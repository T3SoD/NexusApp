using System.Net;
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// The fetch cycle, tested with a fake transport: no network, no dispatcher (Start is never
// called here, so the hourly DispatcherTimer is never created). The properties that must hold:
// datasets are independent (a failed endpoint keeps its previous rows and stamp), the refined
// union merges per commodity id, the reference datasets obey their 24h interval, the cycle is
// single-flight, and the auto path is inert when the toggle is off or the profile is demo.
public class MarketDataServiceTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    public void Dispose()
    {
        foreach (var d in _tempDirs)
        {
            try { if (Directory.Exists(d)) Directory.Delete(d, recursive: true); }
            catch { /* best effort: a stray temp dir must never fail a green test */ }
        }
    }

    // Per-URL canned bodies plus a request log, mirroring UpdateServiceTests.FakeTransport.
    // An unseeded URL behaves like the real transport's EnsureSuccessStatusCode on a 404.
    private sealed class FakeTransport : IMarketDataTransport
    {
        public Dictionary<string, string> Responses { get; } = new();
        public Dictionary<string, Exception> Throws { get; } = new();
        public List<string> Requested { get; } = new();

        // Parking: a request for GateUrl, or the (GateAfterMatches + 1)-th request whose url
        // starts with GatePrefix, waits until Gate is completed OR the cycle token is cancelled.
        // That is how a real in-flight HttpClient call behaves, and it makes "cancel a cycle that
        // is parked mid-request" deterministic without a single sleep.
        public string? GateUrl { get; set; }
        public string? GatePrefix { get; set; }
        public int GateAfterMatches { get; set; }
        public TaskCompletionSource<bool> Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _prefixMatches;

        public async Task<string> GetStringAsync(string url, int maxBytes, CancellationToken ct)
        {
            lock (Requested) Requested.Add(url);

            var park = GateUrl is not null && url == GateUrl;
            if (!park && GatePrefix is not null && url.StartsWith(GatePrefix, StringComparison.Ordinal))
                park = Interlocked.Increment(ref _prefixMatches) > GateAfterMatches;
            if (park)
            {
                var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                using (ct.Register(() => cancelled.TrySetResult(true)))
                    await Task.WhenAny(Gate.Task, cancelled.Task).ConfigureAwait(false);
            }

            // The real transport hands the cycle token to HttpClient, so a cancelled cycle throws
            // here rather than completing; the fake honors it the same way.
            ct.ThrowIfCancellationRequested();
            if (Throws.TryGetValue(url, out var ex)) throw ex;
            if (!Responses.TryGetValue(url, out var body))
                throw new HttpRequestException("Response status code does not indicate success: 404 (Not Found).", null, HttpStatusCode.NotFound);
            if (body.Length > maxBytes) throw new InvalidOperationException("response larger than expected");
            return body;
        }

        public int CountOf(string url)
        {
            lock (Requested) return Requested.Count(u => u == url);
        }
    }

    private static string Url(string endpoint) => MarketDataService.BaseUrl + endpoint;
    // The one price leg, per commodity id: no bulk endpoint returns the full row shape.
    private static string RefinedUrl(int id) => Url($"commodities_prices?id_commodity={id}");

    private const string GameVersionsBody = """{"status":"ok","data":{"live":"4.9.1","ptu":"4.10.0"}}""";

    // Two mapped raw commodities ("Bexalite (Raw)" and "Gold (Ore)" are both in
    // MarketNameMap.SeedToUexRaw) with their refined parents, so the cycle resolves ids 11 and 21.
    private const string CommoditiesBody = """
        {"status":"ok","data":[
          {"id":10,"name":"Bexalite (Raw)","slug":"bexalite-raw","is_raw":1,"is_refined":0,"id_parent":11},
          {"id":11,"name":"Bexalite","slug":"bexalite","is_raw":0,"is_refined":1,"id_parent":0},
          {"id":20,"name":"Gold (Ore)","slug":"gold-ore","is_raw":1,"is_refined":0,"id_parent":21},
          {"id":21,"name":"Gold","slug":"gold","is_raw":0,"is_refined":1,"id_parent":0}
        ]}
        """;

    private const string YieldsBody = """
        {"status":"ok","data":[
          {"id_terminal":300,"id_commodity":11,"value":12,"value_week":10,"date_modified":1750000000,"terminal_name":"Refinery A"}
        ]}
        """;

    private const string TerminalsBody = """
        {"status":"ok","data":[
          {"id":300,"name":"Refinery A","type":"refinery","is_refinery":1,"star_system_name":"Stanton","space_station_name":"Port Olisar"}
        ]}
        """;

    private static string PricesBody(params (int terminal, int commodity, int sell)[] rows) =>
        "{\"status\":\"ok\",\"data\":[" + string.Join(",", rows.Select(r =>
            $"{{\"id_terminal\":{r.terminal},\"id_commodity\":{r.commodity},\"price_sell\":{r.sell}," +
            $"\"price_sell_avg_week\":{r.sell},\"game_version\":\"4.9.1\",\"date_modified\":1750000000," +
            $"\"terminal_name\":\"Terminal {r.terminal}\"}}")) + "]}";

    private const string TradePricesAllBody = """
        {"status":"ok","data":[
          {"id_terminal":400,"id_commodity":11,"price_buy":0,"price_sell":8500,
           "scu_buy":0,"scu_sell_stock":1200,"status_buy":0,"status_sell":3,
           "container_sizes":"1,2,4,8,16,24,32","date_modified":1750000000,
           "terminal_name":"Trade Terminal 400","commodity_name":"Bexalite"}
        ],"message":""}
        """;

    private static void SeedAll(FakeTransport t)
    {
        t.Responses[Url("game_versions")] = GameVersionsBody;
        t.Responses[Url("commodities")] = CommoditiesBody;
        t.Responses[RefinedUrl(11)] = PricesBody((200, 11, 500));
        t.Responses[RefinedUrl(21)] = PricesBody((201, 21, 600));
        t.Responses[Url("commodities_prices_all")] = TradePricesAllBody;
        t.Responses[Url("refineries_yields")] = YieldsBody;
        t.Responses[Url("terminals")] = TerminalsBody;
    }

    private (MarketDataService svc, FakeTransport t, SettingsService settings, string snapshotPath) Make(
        bool? enabled = true, bool demo = false, Func<bool>? isForegroundRelevant = null)
    {
        var t = new FakeTransport();
        var dir = Path.Combine(Path.GetTempPath(), "nexus-market-svc-test-" + Path.GetRandomFileName());
        _tempDirs.Add(dir);
        Directory.CreateDirectory(dir);
        var settings = new SettingsService(Path.Combine(dir, "settings.json"));
        settings.Current.MarketDataEnabled = enabled;
        var snapshotPath = Path.Combine(dir, "cache", "uex_snapshot.json");
        return (new MarketDataService(settings, t, snapshotPath, demo, isForegroundRelevant), t, settings, snapshotPath);
    }

    // Relative to now, never a fixed calendar date: a hardcoded stamp would silently change
    // meaning against the 24h reference interval as the wall clock moves past it.
    private static DateTime OldStamp => DateTime.UtcNow - TimeSpan.FromHours(48);

    // A previous run's snapshot: the same four commodities plus refined rows for both ids, all
    // stamped in the past so the cycle sees them as carried-over data. The raw rows are here to
    // prove the retired dataset is carried untouched, never refreshed and never wiped.
    private static MarketSnapshot PreviousSnapshot(DateTime stamp, DateTime? referenceStamp = null) => new()
    {
        Schema = 1,
        LiveGameVersion = "4.8.0",
        Commodities = new MarketDataset<MarketCommodity>
        {
            FetchedUtc = stamp,
            Rows = new List<MarketCommodity>
            {
                new(10, "Bexalite (Raw)", "bexalite-raw", true, false, 11),
                new(11, "Bexalite", "bexalite", false, true, 0),
                new(20, "Gold (Ore)", "gold-ore", true, false, 21),
                new(21, "Gold", "gold", false, true, 0),
            },
        },
        RawPrices = new MarketDataset<MarketPriceRow>
        {
            FetchedUtc = stamp,
            Rows = new List<MarketPriceRow>
            {
                new(800, 10, 11, 11, "4.8.0", stamp, "Old outpost 10"),
                new(801, 20, 22, 22, "4.8.0", stamp, "Old outpost 20"),
            },
        },
        RefinedPrices = new MarketDataset<MarketPriceRow>
        {
            FetchedUtc = stamp,
            Rows = new List<MarketPriceRow>
            {
                new(900, 11, 111, 111, "4.8.0", stamp, "Old refinery 11"),
                new(901, 21, 222, 222, "4.8.0", stamp, "Old refinery 21"),
            },
        },
        TradePrices = new MarketDataset<TradePriceRow>
        {
            FetchedUtc = stamp,
            Rows = new List<TradePriceRow>
            {
                new(950, 11, 0, 8500, 0, 1200, 0, 3, "1,2,4,8,16,24,32", stamp, "Old trade terminal 11", "Bexalite"),
            },
        },
        Yields = new MarketDataset<MarketYieldRow> { FetchedUtc = referenceStamp ?? stamp },
        Terminals = new MarketDataset<MarketTerminal> { FetchedUtc = referenceStamp ?? stamp },
    };

    // ── (a) ShouldFetch truth table ────────────────────────────────────────────

    // The gate reads BOTH hourly dataset stamps, not the newest one across the snapshot.
    [Fact]
    public void ShouldFetch_FiresWhenEitherHourlyDatasetIsStale()
    {
        var now = new DateTime(2026, 7, 27, 12, 0, 0, DateTimeKind.Utc);
        var stale = now - TimeSpan.FromHours(2);
        var fresh = now - TimeSpan.FromMinutes(5);

        // commodities, refinedPrices
        Assert.False(MarketDataService.ShouldFetch(true, false, fresh, fresh, now));   // both fresh
        Assert.True(MarketDataService.ShouldFetch(true, false, stale, fresh, now));    // catalogue stale
        Assert.True(MarketDataService.ShouldFetch(true, false, fresh, stale, now));    // prices stale
        Assert.True(MarketDataService.ShouldFetch(true, false, stale, stale, now));    // both stale
        Assert.True(MarketDataService.ShouldFetch(true, false, DateTime.MinValue, DateTime.MinValue, now));   // never fetched

        // Exactly on the interval boundary counts as due, whichever dataset is on it.
        Assert.True(MarketDataService.ShouldFetch(true, false, now - MarketDataService.RefreshInterval, fresh, now));
        Assert.True(MarketDataService.ShouldFetch(true, false, fresh, now - MarketDataService.RefreshInterval, now));

        // A stamp in the future (clock rollback) self-heals on either side instead of freezing
        // the auto path forever behind a negative subtraction.
        Assert.True(MarketDataService.ShouldFetch(true, false, now + TimeSpan.FromDays(3), fresh, now));
        Assert.True(MarketDataService.ShouldFetch(true, false, fresh, now + TimeSpan.FromDays(3), now));
    }

    // The live failure this gate was rewritten for: a cycle landed the catalogue (and the daily
    // reference data) but no price call, so the price surfaces are empty. Reading only the newest
    // stamp across the snapshot called that "fresh" and suppressed the auto path for an hour.
    [Fact]
    public void ShouldFetch_FreshCatalogueButNoPricesEverFetched_StillFetches()
    {
        var now = new DateTime(2026, 7, 27, 12, 0, 0, DateTimeKind.Utc);
        var fresh = now - TimeSpan.FromMinutes(5);

        Assert.True(MarketDataService.ShouldFetch(true, false, fresh, DateTime.MinValue, now));
    }

    [Fact]
    public void ShouldFetch_ConsentAndDemoProfileGateItRegardlessOfStaleness()
    {
        var now = new DateTime(2026, 7, 27, 12, 0, 0, DateTimeKind.Utc);
        var stale = now - TimeSpan.FromHours(2);

        Assert.False(MarketDataService.ShouldFetch(false, false, stale, stale, now));   // declined
        Assert.False(MarketDataService.ShouldFetch(null, false, stale, stale, now));    // not asked yet
        Assert.False(MarketDataService.ShouldFetch(true, true, stale, stale, now));     // demo profile
        Assert.False(MarketDataService.ShouldFetch(true, true, DateTime.MinValue, DateTime.MinValue, now));
        Assert.False(MarketDataService.ShouldFetch(true, true, now + TimeSpan.FromDays(3), now + TimeSpan.FromDays(3), now));
    }

    // ── (b) Full happy cycle ───────────────────────────────────────────────────

    [Fact]
    public async Task Refresh_HappyCycle_FillsEveryDatasetAndStampsSettings()
    {
        var (svc, t, settings, snapshotPath) = Make();
        SeedAll(t);
        var changed = 0;
        svc.Changed += () => Interlocked.Increment(ref changed);

        await svc.RefreshAsync(manual: true);

        var snap = svc.Snapshot;
        Assert.NotNull(snap);
        Assert.Equal("4.9.1", snap!.LiveGameVersion);
        Assert.Equal(4, snap.Commodities.Rows.Count);
        Assert.Equal(2, snap.RefinedPrices.Rows.Count);
        Assert.Single(snap.Yields.Rows);
        Assert.Single(snap.Terminals.Rows);
        Assert.NotEqual(default, snap.Commodities.FetchedUtc);
        Assert.NotEqual(default, snap.RefinedPrices.FetchedUtc);

        // No raw price leg exists any more: the dataset stays in the schema, empty and unstamped,
        // and the cycle never asks UEX for an ore price under any URL shape.
        Assert.Empty(snap.RawPrices.Rows);
        Assert.Equal(default, snap.RawPrices.FetchedUtc);
        Assert.DoesNotContain(t.Requested, u => u.Contains("commodities_raw_prices", StringComparison.Ordinal));

        // The refined leg asks for the two resolved parent ids and nothing else.
        Assert.Equal(1, t.CountOf(RefinedUrl(11)));
        Assert.Equal(1, t.CountOf(RefinedUrl(21)));
        Assert.Equal(2, t.Requested.Count(u => u.StartsWith(Url("commodities_prices?"), StringComparison.Ordinal)));
        // The bulk trading-tab endpoint is a separate, single call, not folded into that count.
        Assert.Equal(1, t.CountOf(Url("commodities_prices_all")));
        Assert.Single(snap.TradePrices.Rows);
        Assert.Equal(11, snap.TradePrices.Rows[0].CommodityId);
        Assert.NotEqual(default, snap.TradePrices.FetchedUtc);
        Assert.Contains(snap.RefinedPrices.Rows, r => r.CommodityId == 11 && r.TerminalId == 200);
        Assert.Contains(snap.RefinedPrices.Rows, r => r.CommodityId == 21 && r.TerminalId == 201);

        Assert.True(File.Exists(snapshotPath));
        Assert.NotNull(settings.Current.LastMarketFetchUtc);
        Assert.Equal(1, changed);
        Assert.Null(svc.LastError);
        Assert.False(svc.FetchInProgress);
    }

    [Fact]
    public async Task Refresh_HappyCycle_SnapshotSurvivesAReload()
    {
        var (svc, t, _, snapshotPath) = Make();
        SeedAll(t);
        await svc.RefreshAsync(manual: true);

        var reloaded = MarketSnapshotFile.Load(snapshotPath, out var reason);
        Assert.Null(reason);
        Assert.NotNull(reloaded);
        Assert.Equal("4.9.1", reloaded!.LiveGameVersion);
        Assert.Equal(2, reloaded.RefinedPrices.Rows.Count);
    }

    // ── (c) Dataset independence ───────────────────────────────────────────────

    [Fact]
    public async Task Refresh_OneEndpointFails_KeepsThatDatasetAndRefreshesTheRest()
    {
        var (svc, t, _, snapshotPath) = Make();
        var previousStamp = OldStamp;
        MarketSnapshotFile.Save(snapshotPath, PreviousSnapshot(previousStamp));
        svc.LoadSnapshotFromDisk();

        SeedAll(t);
        t.Responses[Url("commodities")] = "<html><body>502 Bad Gateway</body></html>";
        var changed = 0;
        svc.Changed += () => Interlocked.Increment(ref changed);

        await svc.RefreshAsync(manual: true);

        var snap = svc.Snapshot!;
        // The failed dataset keeps BOTH its rows and its stamp.
        Assert.Equal(4, snap.Commodities.Rows.Count);
        Assert.Equal(previousStamp, snap.Commodities.FetchedUtc);
        // Everything else refreshed anyway.
        Assert.Equal("4.9.1", snap.LiveGameVersion);
        Assert.Equal(2, snap.RefinedPrices.Rows.Count);
        Assert.True(snap.RefinedPrices.FetchedUtc > previousStamp);
        Assert.Single(snap.Terminals.Rows);
        // The carried-over commodity rows still drive the per-id refined leg.
        Assert.Equal(1, t.CountOf(RefinedUrl(11)));
        Assert.Equal(1, t.CountOf(RefinedUrl(21)));
        // The retired raw dataset is carried verbatim: same rows, same stamp, never requested.
        Assert.Equal(2, snap.RawPrices.Rows.Count);
        Assert.Equal(previousStamp, snap.RawPrices.FetchedUtc);
        Assert.DoesNotContain(t.Requested, u => u.Contains("commodities_raw_prices", StringComparison.Ordinal));

        Assert.NotNull(svc.LastError);
        Assert.Equal(1, changed);
    }

    [Fact]
    public async Task Refresh_CleanCycleAfterAFailedOne_ClearsLastError()
    {
        var (svc, t, _, _) = Make();
        SeedAll(t);
        t.Responses[Url("commodities")] = "<html>502</html>";
        await svc.RefreshAsync(manual: true);
        Assert.NotNull(svc.LastError);

        t.Responses[Url("commodities")] = CommoditiesBody;
        await svc.RefreshAsync(manual: true);
        Assert.Null(svc.LastError);
    }

    // A well formed but empty array is how UEX says "nothing listed". It must not be counted as
    // a failure, and it must not wipe rows that are merely stale.
    [Fact]
    public async Task Refresh_EmptyButValidResponse_KeepsPreviousRowsWithoutAnError()
    {
        var (svc, t, _, snapshotPath) = Make();
        var previousStamp = OldStamp;
        MarketSnapshotFile.Save(snapshotPath, PreviousSnapshot(previousStamp));
        svc.LoadSnapshotFromDisk();

        SeedAll(t);
        t.Responses[Url("commodities")] = """{"status":"ok","data":[]}""";

        await svc.RefreshAsync(manual: true);

        var snap = svc.Snapshot!;
        Assert.Equal(4, snap.Commodities.Rows.Count);
        Assert.Equal(previousStamp, snap.Commodities.FetchedUtc);
        Assert.Null(svc.LastError);
    }

    // ── (d) Per-id partial failures ────────────────────────────────────────────

    [Fact]
    public async Task Refresh_OneRefinedIdFails_KeepsOnlyThatIdsPreviousRows()
    {
        var (svc, t, _, snapshotPath) = Make();
        var previousStamp = OldStamp;
        MarketSnapshotFile.Save(snapshotPath, PreviousSnapshot(previousStamp));
        svc.LoadSnapshotFromDisk();

        SeedAll(t);
        t.Throws[RefinedUrl(21)] = new HttpRequestException("connection reset");

        await svc.RefreshAsync(manual: true);

        var refined = svc.Snapshot!.RefinedPrices.Rows;
        Assert.Equal(2, refined.Count);
        // Id 11 replaced by the fresh row, id 21 kept from the previous snapshot.
        Assert.Contains(refined, r => r.CommodityId == 11 && r.TerminalId == 200);
        Assert.DoesNotContain(refined, r => r.CommodityId == 11 && r.TerminalId == 900);
        Assert.Contains(refined, r => r.CommodityId == 21 && r.TerminalId == 901);
        Assert.NotNull(svc.LastError);
    }

    // ── (e) Reference interval ─────────────────────────────────────────────────

    [Fact]
    public async Task Refresh_ReferenceDatasetsFresh_AreNotRequested()
    {
        var (svc, t, _, snapshotPath) = Make();
        MarketSnapshotFile.Save(snapshotPath,
            PreviousSnapshot(OldStamp, referenceStamp: DateTime.UtcNow));
        svc.LoadSnapshotFromDisk();
        SeedAll(t);

        await svc.RefreshAsync(manual: true);

        Assert.DoesNotContain(Url("refineries_yields"), t.Requested);
        Assert.DoesNotContain(Url("terminals"), t.Requested);
    }

    [Fact]
    public async Task Refresh_ReferenceDatasetsStale_AreRequested()
    {
        var (svc, t, _, snapshotPath) = Make();
        MarketSnapshotFile.Save(snapshotPath,
            PreviousSnapshot(OldStamp, referenceStamp: DateTime.UtcNow - TimeSpan.FromHours(25)));
        svc.LoadSnapshotFromDisk();
        SeedAll(t);

        await svc.RefreshAsync(manual: true);

        Assert.Contains(Url("refineries_yields"), t.Requested);
        Assert.Contains(Url("terminals"), t.Requested);
        Assert.Single(svc.Snapshot!.Yields.Rows);
    }

    // ── (f) Single flight ──────────────────────────────────────────────────────

    [Fact]
    public async Task Refresh_TwoConcurrentCalls_OnlyOneCycleRuns()
    {
        var (svc, t, _, _) = Make();
        SeedAll(t);
        t.GateUrl = Url("game_versions");

        var first = svc.RefreshAsync(manual: true);
        var second = svc.RefreshAsync(manual: false);

        // The second call loses the Interlocked race before its first await, so it is already done.
        Assert.True(second.IsCompleted);
        Assert.True(svc.FetchInProgress);

        t.Gate.SetResult(true);
        await first;
        await second;

        Assert.Equal(1, t.CountOf(Url("game_versions")));
        Assert.Equal(1, t.CountOf(Url("commodities")));
        Assert.False(svc.FetchInProgress);
    }

    // ── (g) Toggle gating ──────────────────────────────────────────────────────

    [Fact]
    public void MaybeAutoRefresh_Disabled_MakesNoRequests()
    {
        var (svc, t, _, _) = Make(enabled: false);
        SeedAll(t);

        svc.MaybeAutoRefresh();

        Assert.Empty(t.Requested);
        Assert.Null(svc.Snapshot);
    }

    [Fact]
    public void MaybeAutoRefresh_NotAskedYet_MakesNoRequests()
    {
        var (svc, t, _, _) = Make(enabled: null);
        SeedAll(t);

        svc.MaybeAutoRefresh();

        Assert.Empty(t.Requested);
    }

    // The Settings "Refresh now" button is only reachable while the toggle is on, so an explicit
    // call always runs: the toggle gates the AUTO path, not the manual one.
    [Fact]
    public async Task Refresh_ManualWithToggleOff_StillRuns()
    {
        var (svc, t, _, _) = Make(enabled: false);
        SeedAll(t);

        await svc.RefreshAsync(manual: true);

        Assert.NotEmpty(t.Requested);
        Assert.Equal("4.9.1", svc.Snapshot!.LiveGameVersion);
    }

    // ── (h) Demo profile ───────────────────────────────────────────────────────

    [Fact]
    public void MaybeAutoRefresh_DemoProfile_MakesNoRequests()
    {
        var (svc, t, _, _) = Make(enabled: true, demo: true);
        SeedAll(t);

        svc.MaybeAutoRefresh();

        Assert.Empty(t.Requested);
        Assert.Null(svc.Snapshot);
    }

    // ── Disk load and disposal ─────────────────────────────────────────────────

    [Fact]
    public void LoadSnapshotFromDisk_MissingOrCorruptFile_LeavesSnapshotNull()
    {
        var (svc, _, _, snapshotPath) = Make();
        svc.LoadSnapshotFromDisk();
        Assert.Null(svc.Snapshot);

        Directory.CreateDirectory(Path.GetDirectoryName(snapshotPath)!);
        File.WriteAllText(snapshotPath, "{ not json");
        svc.LoadSnapshotFromDisk();
        Assert.Null(svc.Snapshot);
    }

    // Cancelling the cycle (Dispose here, the two-minute deadline in production) takes the same
    // path: the endpoints that already landed are still published, the rest are skipped, and
    // LastError says the cycle did not finish.
    [Fact]
    public async Task Refresh_CycleCancelledMidway_PublishesWhatLandedAndNotesIt()
    {
        var (svc, t, settings, snapshotPath) = Make();
        SeedAll(t);
        t.GateUrl = Url("commodities");
        var changed = 0;
        svc.Changed += () => Interlocked.Increment(ref changed);

        var cycle = svc.RefreshAsync(manual: true);
        svc.Dispose();          // cancels the in-flight cycle through its linked token
        t.Gate.SetResult(true);
        await cycle;

        Assert.Equal("4.9.1", svc.Snapshot!.LiveGameVersion);   // step 1 landed before the cancel
        Assert.Empty(svc.Snapshot.RefinedPrices.Rows);          // the price leg never ran
        Assert.DoesNotContain(t.Requested, u => u.StartsWith(Url("commodities_prices"), StringComparison.Ordinal));
        Assert.NotNull(svc.LastError);
        Assert.Equal(1, changed);
        Assert.True(File.Exists(snapshotPath));
        Assert.NotNull(settings.Current.LastMarketFetchUtc);
    }

    // The per-id refined leg is ~25 of the cycle's ~30 requests, so it is where the deadline
    // usually lands. Everything the leg already fetched must still reach its dataset.
    [Fact]
    public async Task Refresh_CancelledInsideTheRefinedLeg_KeepsTheIdsThatCompleted()
    {
        var (svc, t, _, snapshotPath) = Make();
        var previousStamp = OldStamp;
        MarketSnapshotFile.Save(snapshotPath, PreviousSnapshot(previousStamp));
        svc.LoadSnapshotFromDisk();

        SeedAll(t);
        // Let the first refined id through, park on the second. Which id is first depends on the
        // name map's order, so the assertions below read it from the request log rather than
        // assuming one.
        t.GatePrefix = Url("commodities_prices?");
        t.GateAfterMatches = 1;

        var cycle = svc.RefreshAsync(manual: true);
        svc.Dispose();     // cancels the cycle while it sits on the second refined request
        await cycle;

        var refinedRequests = t.Requested.Where(u => u.StartsWith(Url("commodities_prices?"), StringComparison.Ordinal)).ToList();
        Assert.Equal(2, refinedRequests.Count);
        var completedId = refinedRequests[0].EndsWith("=11", StringComparison.Ordinal) ? 11 : 21;
        var cancelledId = completedId == 11 ? 21 : 11;

        var rows = svc.Snapshot!.RefinedPrices.Rows;
        Assert.Equal(2, rows.Count);
        // The id that came back before the cancel landed fresh...
        Assert.Contains(rows, r => r.CommodityId == completedId && r.TerminalId == (completedId == 11 ? 200 : 201));
        // ...and the one that never returned kept its previous row instead of vanishing.
        Assert.Contains(rows, r => r.CommodityId == cancelledId && r.TerminalId == (cancelledId == 11 ? 900 : 901));
        Assert.True(svc.Snapshot.RefinedPrices.FetchedUtc > previousStamp);
        Assert.NotNull(svc.LastError);
    }

    // Cancelling is not enough on its own: the cycle still writes settings.json and the snapshot
    // file on its way out, and those writes must not land after App.OnExit.
    [Fact]
    public async Task Dispose_WaitsForTheInFlightCycleToFinishItsWrites()
    {
        var (svc, t, settings, snapshotPath) = Make();
        SeedAll(t);
        t.GateUrl = Url("commodities");

        var cycle = svc.RefreshAsync(manual: true);
        Assert.False(cycle.IsCompleted);            // parked mid-cycle

        svc.Dispose();

        // Dispose returned only after the cancelled cycle finished unwinding and writing.
        // The task's own completion flag settles on the pool thread, so give it a bounded
        // asynchronous wait instead of asserting the instantaneous state.
        Assert.Same(cycle, await Task.WhenAny(cycle, Task.Delay(TimeSpan.FromSeconds(5))));
        Assert.True(File.Exists(snapshotPath));
        Assert.NotNull(settings.Current.LastMarketFetchUtc);
        Assert.False(svc.FetchInProgress);
        await cycle;
    }

    [Fact]
    public async Task Dispose_IsIdempotentAndStopsFurtherRefreshes()
    {
        var (svc, t, _, _) = Make();
        SeedAll(t);

        svc.Dispose();
        svc.Dispose();
        await svc.RefreshAsync(manual: true);
        svc.MaybeAutoRefresh();

        Assert.Empty(t.Requested);
    }

    // ── (i) Foreground gating ──────────────────────────────────────────────────

    [Fact]
    public void MaybeAutoRefresh_ForegroundNotRelevant_MakesNoRequests()
    {
        var (svc, t, _, _) = Make(isForegroundRelevant: () => false);
        SeedAll(t);

        svc.MaybeAutoRefresh();

        Assert.Empty(t.Requested);
        Assert.Null(svc.Snapshot);
    }

    [Fact]
    public void MaybeAutoRefresh_ForegroundRelevant_RunsNormally()
    {
        var (svc, t, _, _) = Make(isForegroundRelevant: () => true);
        SeedAll(t);

        svc.MaybeAutoRefresh();

        // MaybeAutoRefresh is fire-and-forget (Task.Run), so the request only lands on the
        // pool thread some time after this call returns; a bounded spin-wait replaces a flaky
        // instantaneous check.
        Assert.True(SpinWait.SpinUntil(() => t.Requested.Count > 0, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void MaybeAutoRefresh_NoForegroundFuncGiven_DefaultsToAlwaysRelevant()
    {
        var (svc, t, _, _) = Make();   // no isForegroundRelevant passed: every pre-existing caller's behavior
        SeedAll(t);

        svc.MaybeAutoRefresh();

        // Same fire-and-forget timing note as above.
        Assert.True(SpinWait.SpinUntil(() => t.Requested.Count > 0, TimeSpan.FromSeconds(2)));
    }

    // The manual path (a "Refresh now" action) is not gated by foreground relevance, matching
    // the existing "manual ignores the consent toggle too" precedent.
    [Fact]
    public async Task Refresh_ManualWithForegroundNotRelevant_StillRuns()
    {
        var (svc, t, _, _) = Make(isForegroundRelevant: () => false);
        SeedAll(t);

        await svc.RefreshAsync(manual: true);

        Assert.NotEmpty(t.Requested);
        Assert.NotNull(svc.Snapshot);
    }

    // ── (j) TradePrices bulk fetch ─────────────────────────────────────────────

    [Fact]
    public async Task Refresh_TradePrices_ParsesBulkEndpointIntoDataset()
    {
        var (svc, t, _, _) = Make();
        SeedAll(t);

        await svc.RefreshAsync(manual: true);

        var row = Assert.Single(svc.Snapshot!.TradePrices.Rows);
        Assert.Equal(400, row.TerminalId);
        Assert.Equal(11, row.CommodityId);
        Assert.Equal(8500, row.Sell);
        Assert.NotEqual(default, svc.Snapshot.TradePrices.FetchedUtc);
    }

    [Fact]
    public async Task Refresh_TradePricesEndpointFails_KeepsPreviousRowsAndStamp()
    {
        var (svc, t, _, snapshotPath) = Make();
        var previousStamp = OldStamp;
        MarketSnapshotFile.Save(snapshotPath, PreviousSnapshot(previousStamp));
        svc.LoadSnapshotFromDisk();

        SeedAll(t);
        t.Responses[Url("commodities_prices_all")] = "<html>502</html>";

        await svc.RefreshAsync(manual: true);

        var snap = svc.Snapshot!;
        Assert.Single(snap.TradePrices.Rows);
        Assert.Equal(950, snap.TradePrices.Rows[0].TerminalId);   // the OLD row, not replaced
        Assert.Equal(previousStamp, snap.TradePrices.FetchedUtc);
        Assert.NotNull(svc.LastError);
    }

    [Fact]
    public async Task Refresh_TradePricesEmptyButValid_KeepsPreviousRowsWithoutAnError()
    {
        var (svc, t, _, snapshotPath) = Make();
        var previousStamp = OldStamp;
        MarketSnapshotFile.Save(snapshotPath, PreviousSnapshot(previousStamp));
        svc.LoadSnapshotFromDisk();

        SeedAll(t);
        t.Responses[Url("commodities_prices_all")] = """{"status":"ok","data":[]}""";

        await svc.RefreshAsync(manual: true);

        var snap = svc.Snapshot!;
        Assert.Single(snap.TradePrices.Rows);
        Assert.Equal(previousStamp, snap.TradePrices.FetchedUtc);
        Assert.Null(svc.LastError);
    }
}
