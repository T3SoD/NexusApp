using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

public class SctMarketServiceTests : IDisposable
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

    private sealed class FakeSctTransport : ISctTransport
    {
        public Dictionary<string, string> Responses { get; } = new();
        public List<string> Requested { get; } = new();

        // Parking: a request for GateUrl waits until Gate is completed OR the cycle token is
        // cancelled - the same idiom as MarketDataServiceTests.FakeTransport, needed here to make
        // "Dispose cancels a refresh parked mid-request" deterministic without a sleep.
        public string? GateUrl { get; set; }
        public TaskCompletionSource<bool> Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // How long a parked request lingers AFTER cancellation before it actually throws,
        // simulating a real unwind that takes measurable time. Without this, cancelling and
        // NOT waiting (the bug the drain fixes) is indistinguishable from cancelling and
        // waiting, because the fake would unwind instantly either way.
        public TimeSpan CancelUnwindDelay { get; set; } = TimeSpan.Zero;

        public async Task<string> GetStringAsync(string url, int maxBytes, CancellationToken ct)
        {
            Requested.Add(url);
            if (GateUrl is not null && url == GateUrl)
            {
                var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                using (ct.Register(() => cancelled.TrySetResult(true)))
                    await Task.WhenAny(Gate.Task, cancelled.Task).ConfigureAwait(false);
                if (CancelUnwindDelay > TimeSpan.Zero)
                    await Task.Delay(CancelUnwindDelay).ConfigureAwait(false);   // no ct: models real unwind latency
            }
            ct.ThrowIfCancellationRequested();
            if (!Responses.TryGetValue(url, out var body))
                throw new HttpRequestException("404 (Not Found)", null, System.Net.HttpStatusCode.NotFound);
            return body;
        }
    }

    private static string PageUrl(int page) =>
        $"{SctMarketService.BaseUrl}crowdsource/commodity-listings?page={page}";

    // 8 fresh-and-mapped rows (mic-l2), 3 fresh-but-ambiguous ("nyx gateway"), 3 fresh-but-typo
    // ("sheperd's rest") - same real values as SctListingParserTests' RealPageBody, trimmed to the
    // rows this test needs.
    private const string Page0Body = """
    {"content":[
      {"location":"stanton > mic l2 > mic-l2 long forest station","transaction":"SELLS","commodity":"waste","price":115,"quantity":1,"saturation":0.6666666666666666,"boxSizesInScu":null,"batchId":"dcc4ba21-b697-4851-8982-32d7d1a49141","timestamp":"2026-07-29T10:32:38-04:00"},
      {"location":"stanton > mic l2 > mic-l2 long forest station","transaction":"SELLS","commodity":"scrap","price":2990,"quantity":2100,"saturation":1.0,"boxSizesInScu":null,"batchId":"dcc4ba21-b697-4851-8982-32d7d1a49141","timestamp":"2026-07-29T10:32:38-04:00"},
      {"location":"stanton > mic l2 > mic-l2 long forest station","transaction":"SELLS","commodity":"diamond","price":5759,"quantity":9,"saturation":0.16666666666666666,"boxSizesInScu":null,"batchId":"dcc4ba21-b697-4851-8982-32d7d1a49141","timestamp":"2026-07-29T10:32:38-04:00"},
      {"location":"stanton > mic l2 > mic-l2 long forest station","transaction":"SELLS","commodity":"astatine","price":2649,"quantity":6,"saturation":0.16666666666666666,"boxSizesInScu":null,"batchId":"dcc4ba21-b697-4851-8982-32d7d1a49141","timestamp":"2026-07-29T10:32:38-04:00"},
      {"location":"nyx gateway","transaction":"BUYS","commodity":"stileron","price":150000,"quantity":95,"saturation":0.8333333333333334,"boxSizesInScu":null,"batchId":"b5f9bd7d-e8d0-41d0-926f-1c7669193699","timestamp":"2026-07-28T10:52:49-04:00"},
      {"location":"nyx gateway","transaction":"BUYS","commodity":"stims","price":5500,"quantity":0,"saturation":0.0,"boxSizesInScu":null,"batchId":"b5f9bd7d-e8d0-41d0-926f-1c7669193699","timestamp":"2026-07-28T10:52:49-04:00"},
      {"location":"nyx gateway","transaction":"BUYS","commodity":"medical supplies","price":4800,"quantity":321,"saturation":0.3333333333333333,"boxSizesInScu":null,"batchId":"b5f9bd7d-e8d0-41d0-926f-1c7669193699","timestamp":"2026-07-28T10:52:49-04:00"},
      {"location":"sheperd's rest","transaction":"BUYS","commodity":"processed food","price":1200,"quantity":0,"saturation":0.0,"boxSizesInScu":null,"batchId":"f5a8cd81-0c92-4b20-8f80-625d239d0729","timestamp":"2026-07-25T16:00:32-04:00"},
      {"location":"sheperd's rest","transaction":"BUYS","commodity":"revenant pod","price":11000,"quantity":0,"saturation":0.0,"boxSizesInScu":null,"batchId":"f5a8cd81-0c92-4b20-8f80-625d239d0729","timestamp":"2026-07-25T16:00:32-04:00"},
      {"location":"sheperd's rest","transaction":"BUYS","commodity":"nitrogen","price":3000,"quantity":0,"saturation":0.0,"boxSizesInScu":null,"batchId":"f5a8cd81-0c92-4b20-8f80-625d239d0729","timestamp":"2026-07-25T16:00:32-04:00"}
    ],"page":{"size":100,"number":0,"totalElements":10,"totalPages":1}}
    """;

    private const string EmptyPageBody = """{"content":[],"page":{"size":100,"number":1,"totalElements":10,"totalPages":1}}""";

    // 2 fresh + mapped, 2 real, mapped, but >7d stale (baijini point corundum, nyx > levski
    // laranite) - proves the cutoff runs even on rows the map DOES resolve.
    private const string Page0WithStaleBody = """
    {"content":[
      {"location":"stanton > mic l2 > mic-l2 long forest station","transaction":"SELLS","commodity":"waste","price":115,"quantity":1,"saturation":0.6666666666666666,"boxSizesInScu":null,"batchId":"dcc4ba21-b697-4851-8982-32d7d1a49141","timestamp":"2026-07-29T10:32:38-04:00"},
      {"location":"stanton > mic l2 > mic-l2 long forest station","transaction":"SELLS","commodity":"scrap","price":2990,"quantity":2100,"saturation":1.0,"boxSizesInScu":null,"batchId":"dcc4ba21-b697-4851-8982-32d7d1a49141","timestamp":"2026-07-29T10:32:38-04:00"},
      {"location":"stanton > arccorp > baijini point","transaction":"SELLS","commodity":"corundum","price":3015,"quantity":1144,"saturation":0.3333333333333333,"boxSizesInScu":null,"batchId":"f5bced9d-0470-459b-9e05-65c2903cbcfb","timestamp":"2026-07-19T07:06:10-04:00"},
      {"location":"nyx > levski","transaction":"BUYS","commodity":"laranite","price":7800,"quantity":0,"saturation":0.0,"boxSizesInScu":null,"batchId":"9e4adc86-f1b9-4464-afad-1beb034f624c","timestamp":"2026-07-19T11:28:57-04:00"}
    ],"page":{"size":100,"number":0,"totalElements":4,"totalPages":1}}
    """;

    // Two BUYS + one SELLS at "Pyro > Bloom > Frigid Knot" (an SCT-only location: the embedded
    // map's own note is "SCT-only (UEX item shop 558)", commodityId AND rawId both null), plus one
    // BUYS at "Stanton > Hurston > Everus Harbor" (a normal UEX-mapped terminal, commodityId 25),
    // all for the same commodity (Corundum, uexId 22) - exercises SctOnlyBuyers' two independent
    // filters (BUYS side, and the location having NO UEX terminal at all) against each other.
    private const string SctOnlyLocationBody = """
    {"content":[
      {"location":"pyro > bloom > frigid knot","transaction":"BUYS","commodity":"corundum","price":3000,"quantity":50,"saturation":0.5,"boxSizesInScu":null,"batchId":"11111111-1111-1111-1111-111111111111","timestamp":"2026-07-29T10:00:00-04:00"},
      {"location":"pyro > bloom > frigid knot","transaction":"SELLS","commodity":"corundum","price":3100,"quantity":10,"saturation":0.2,"boxSizesInScu":null,"batchId":"11111111-1111-1111-1111-111111111111","timestamp":"2026-07-29T10:00:00-04:00"},
      {"location":"stanton > hurston > everus harbor","transaction":"BUYS","commodity":"corundum","price":2900,"quantity":80,"saturation":0.4,"boxSizesInScu":null,"batchId":"22222222-2222-2222-2222-222222222222","timestamp":"2026-07-29T09:00:00-04:00"}
    ],"page":{"size":100,"number":0,"totalElements":3,"totalPages":1}}
    """;

    private static readonly DateTime NowUtc = new(2026, 7, 29, 18, 0, 0, DateTimeKind.Utc);

    private (SctMarketService svc, FakeSctTransport t, SettingsService settings) Make(bool enabled)
    {
        var (svc, t, settings, _) = MakeWithPath(enabled);
        return (svc, t, settings);
    }

    // Same as Make, plus the snapshot path: the preserve-the-previous-snapshot tests assert on the
    // on-disk file as well as the in-memory one.
    private (SctMarketService svc, FakeSctTransport t, SettingsService settings, string snapshotPath) MakeWithPath(
        bool enabled, Func<bool>? isForegroundRelevant = null)
    {
        var dir = Directory.CreateTempSubdirectory("nexus-sct-test").FullName;
        _tempDirs.Add(dir);
        var settings = new SettingsService(Path.Combine(dir, "settings.json"));
        settings.Current.MarketDataEnabled = enabled;
        var t = new FakeSctTransport();
        var snapshotPath = Path.Combine(dir, "sct_snapshot.json");
        var svc = new SctMarketService(settings, t, snapshotPath, isForegroundRelevant);
        return (svc, t, settings, snapshotPath);
    }

    [Fact]
    public async Task RefreshAsync_FlagOff_TransportNeverCalled()
    {
        var (svc, t, _) = Make(enabled: false);
        await svc.RefreshAsync(manual: true, NowUtc);
        Assert.Empty(t.Requested);
    }

    [Fact]
    public void Start_FlagOff_NeverTouchesDisk()
    {
        var (svc, t, _) = Make(enabled: false);
        svc.Start();
        Assert.Empty(t.Requested);   // Start() checks the flag first too - fully inert while off
    }

    [Fact]
    public async Task RefreshAsync_FlagOn_JoinDropsAmbiguousAndTypoLocations()
    {
        var (svc, t, _) = Make(enabled: true);
        t.Responses[PageUrl(0)] = Page0Body;
        t.Responses[PageUrl(1)] = EmptyPageBody;

        await svc.RefreshAsync(manual: true, NowUtc);

        Assert.Equal(4, svc.Snapshot!.Rows.Count);   // only the 4 mic-l2 rows resolve in the map
        Assert.DoesNotContain(svc.Snapshot.Rows, r => r.Location == "nyx gateway");
        Assert.DoesNotContain(svc.Snapshot.Rows, r => r.Location == "sheperd's rest");
    }

    [Fact]
    public async Task RefreshAsync_FlagOn_CutoffDropsStaleRowsEvenWhenMapped()
    {
        var (svc, t, _) = Make(enabled: true);
        t.Responses[PageUrl(0)] = Page0WithStaleBody;
        t.Responses[PageUrl(1)] = EmptyPageBody;

        await svc.RefreshAsync(manual: true, NowUtc);

        Assert.Equal(2, svc.Snapshot!.Rows.Count);
        Assert.All(svc.Snapshot.Rows, r => Assert.True(NowUtc - r.TimestampUtc <= SctMarketService.MaxListingAge));
    }

    [Fact]
    public async Task RefreshAsync_PagesUntilEmpty_ThenStops()
    {
        var (svc, t, _) = Make(enabled: true);
        t.Responses[PageUrl(0)] = Page0Body;
        t.Responses[PageUrl(1)] = EmptyPageBody;

        await svc.RefreshAsync(manual: true, NowUtc);

        Assert.Equal(2, t.Requested.Count);   // page 0, then the empty page 1 that ends the loop
    }

    // Cancelling alone is not enough: the cancelled cycle is still unwinding (still logging, still
    // able to raise Changed into a subscriber) after Dispose returns, and none of that may race
    // whatever runs right after (App.OnExit calls Market.Dispose() then Sct.Dispose() in that
    // order). The fake's CancelUnwindDelay makes the unwind take measurable wall-clock time, so
    // this proves Dispose() itself blocks for it rather than cancelling and returning immediately
    // (a plain "does the refresh eventually finish" assertion would pass either way, since
    // cancellation alone lets the refresh finish on its own shortly after Dispose returns).
    // The cancelled cycle itself publishes NOTHING - no snapshot file at all here, since this
    // service had none before (see the preserve-the-previous-snapshot tests below).
    [Fact]
    public async Task Dispose_BlocksUntilTheInFlightRefreshFinishesUnwinding()
    {
        var dir = Directory.CreateTempSubdirectory("nexus-sct-test").FullName;
        _tempDirs.Add(dir);
        var settings = new SettingsService(Path.Combine(dir, "settings.json"));
        settings.Current.MarketDataEnabled = true;
        var snapshotPath = Path.Combine(dir, "sct_snapshot.json");
        var unwindDelay = TimeSpan.FromMilliseconds(300);
        var t = new FakeSctTransport { GateUrl = PageUrl(0), CancelUnwindDelay = unwindDelay };
        t.Responses[PageUrl(0)] = Page0Body;
        t.Responses[PageUrl(1)] = EmptyPageBody;
        var svc = new SctMarketService(settings, t, snapshotPath);

        var refresh = svc.RefreshAsync(manual: true, NowUtc);
        Assert.False(refresh.IsCompleted);   // parked mid-request on page 0

        var sw = Stopwatch.StartNew();
        svc.Dispose();
        sw.Stop();

        Assert.True(sw.Elapsed >= unwindDelay - TimeSpan.FromMilliseconds(50),
            $"Dispose returned after {sw.Elapsed.TotalMilliseconds:0}ms, expected to block for close to {unwindDelay.TotalMilliseconds:0}ms");
        Assert.True(refresh.IsCompleted);
        Assert.False(File.Exists(snapshotPath));   // a cancelled cycle never writes a snapshot
        Assert.False(svc.FetchInProgress);
        await refresh;
    }

    // --- A failed or cancelled cycle must not replace good data with an empty snapshot ---------
    // Same rule MarketDataService's Carry precedent applies (it keeps prior rows for every dataset
    // a cycle did not successfully replace): a transient network failure must leave the last good
    // in-memory snapshot AND the last good snapshot file exactly as they were.

    [Fact]
    public async Task RefreshAsync_FirstPageFails_KeepsThePreviousSnapshotAndFile()
    {
        var (svc, t, _, snapshotPath) = MakeWithPath(enabled: true);
        t.Responses[PageUrl(0)] = Page0Body;
        t.Responses[PageUrl(1)] = EmptyPageBody;
        await svc.RefreshAsync(manual: true, NowUtc);
        var good = svc.Snapshot!;
        var goodJson = File.ReadAllText(snapshotPath);
        Assert.Equal(4, good.Rows.Count);

        t.Responses.Clear();   // every page now 404s: the whole cycle fails at page 0
        await svc.RefreshAsync(manual: true, NowUtc.AddHours(1));

        Assert.Same(good, svc.Snapshot);                        // in-memory snapshot untouched
        Assert.Equal(goodJson, File.ReadAllText(snapshotPath));   // on-disk snapshot untouched
    }

    [Fact]
    public async Task RefreshAsync_MidPaginationFailure_KeepsThePreviousSnapshotRatherThanPublishingATruncatedOne()
    {
        var (svc, t, _, snapshotPath) = MakeWithPath(enabled: true);
        t.Responses[PageUrl(0)] = Page0Body;
        t.Responses[PageUrl(1)] = EmptyPageBody;
        await svc.RefreshAsync(manual: true, NowUtc);
        var good = svc.Snapshot!;
        var goodJson = File.ReadAllText(snapshotPath);

        // Second cycle: page 0 succeeds with DIFFERENT rows, page 1 throws. Publishing here would
        // hand the UI a partial page-0-only snapshot stamped fresh.
        t.Responses[PageUrl(0)] = SctOnlyLocationBody;
        t.Responses.Remove(PageUrl(1));
        await svc.RefreshAsync(manual: true, NowUtc.AddHours(1));

        Assert.Same(good, svc.Snapshot);
        Assert.Equal(goodJson, File.ReadAllText(snapshotPath));
    }

    [Fact]
    public async Task RefreshAsync_EmptyFirstPageWith200_KeepsThePreviousSnapshotAndFile()
    {
        var (svc, t, _, snapshotPath) = MakeWithPath(enabled: true);
        t.Responses[PageUrl(0)] = Page0Body;
        t.Responses[PageUrl(1)] = EmptyPageBody;
        await svc.RefreshAsync(manual: true, NowUtc);
        var good = svc.Snapshot!;
        var goodJson = File.ReadAllText(snapshotPath);
        Assert.Equal(4, good.Rows.Count);

        // Second cycle: page 0 itself answers 200 with zero rows (an endpoint hiccup, not a
        // failure - cycleOk stays true). Publishing would wipe a good snapshot with an empty one.
        t.Responses[PageUrl(0)] = """{"content":[],"page":{"size":100,"number":0,"totalElements":0,"totalPages":0}}""";
        var fired = 0;
        svc.Changed += () => fired++;
        await svc.RefreshAsync(manual: true, NowUtc.AddHours(1));

        Assert.Same(good, svc.Snapshot);                          // in-memory snapshot untouched
        Assert.Equal(goodJson, File.ReadAllText(snapshotPath));   // on-disk snapshot untouched
        Assert.Equal(0, fired);                                   // nothing published, nothing raised
    }

    [Fact]
    public async Task RefreshAsync_RuntimeOptIn_HydratesFromDisk_SoTheEmptyPageGuardProtectsTheFile()
    {
        // The off-at-launch, toggled-on-later shape: Start's disk load is flag-gated, so a fresh
        // service reaches RefreshAsync with _snapshot null even though a good cache sits on disk.
        // Without the late hydration, the zero-rows guard had nothing to protect and an endpoint
        // hiccup overwrote the good file with a fresh-stamped empty snapshot.
        var (svc1, t1, settings, snapshotPath) = MakeWithPath(enabled: true);
        t1.Responses[PageUrl(0)] = Page0Body;
        t1.Responses[PageUrl(1)] = EmptyPageBody;
        await svc1.RefreshAsync(manual: true, NowUtc);
        var goodJson = File.ReadAllText(snapshotPath);
        svc1.Dispose();

        var t2 = new FakeSctTransport();
        t2.Responses[PageUrl(0)] = """{"content":[],"page":{"size":100,"number":0,"totalElements":0,"totalPages":0}}""";
        using var svc2 = new SctMarketService(settings, t2, snapshotPath);
        await svc2.RefreshAsync(manual: true, NowUtc.AddHours(1));

        Assert.NotNull(svc2.Snapshot);
        Assert.Equal(4, svc2.Snapshot!.Rows.Count);               // the disk rows, published
        Assert.Equal(goodJson, File.ReadAllText(snapshotPath));   // on-disk cache untouched
    }

    [Fact]
    public async Task RefreshAsync_EmptyFirstPage_NoPreviousSnapshot_StillPublishesTheEmptyResult()
    {
        // First-ever fetch: there is nothing to protect, and publishing keeps the honest
        // "fetched, nothing cached" state stamped so the 6h cadence is not re-armed every tick.
        var (svc, t, _, snapshotPath) = MakeWithPath(enabled: true);
        t.Responses[PageUrl(0)] = """{"content":[],"page":{"size":100,"number":0,"totalElements":0,"totalPages":0}}""";

        await svc.RefreshAsync(manual: true, NowUtc);

        Assert.NotNull(svc.Snapshot);
        Assert.Empty(svc.Snapshot!.Rows);
        Assert.Equal(NowUtc, svc.Snapshot.FetchedUtc);
        Assert.True(File.Exists(snapshotPath));
    }

    [Fact]
    public async Task RefreshAsync_FirstPageFails_DoesNotRaiseChanged()
    {
        var (svc, t, _) = Make(enabled: true);
        var fired = 0;
        svc.Changed += () => fired++;

        await svc.RefreshAsync(manual: true, NowUtc);   // no responses registered at all: page 0 fails

        Assert.Equal(0, fired);
        Assert.Null(svc.Snapshot);
    }

    // --- Architect resolution 1: the UI-facing read surface (SnapshotFetchedUtc, Changed, Find,
    // SctOnlyBuyers) is part of the contract even though the brief's own Step 4 code omits it.
    // Every one of these is flag-gated exactly like RefreshAsync/Start above: null/empty while
    // off, live-checked on every call (not just "never populated while off"), and Changed never
    // raised from a code path that only runs when the flag is on.

    [Fact]
    public void SnapshotFetchedUtc_FlagOff_IsNull()
    {
        var (svc, _, _) = Make(enabled: false);
        Assert.Null(svc.SnapshotFetchedUtc);
    }

    [Fact]
    public async Task SnapshotFetchedUtc_FlagOn_NullBeforeRefresh_ThenSetAfterRefresh()
    {
        var (svc, t, _) = Make(enabled: true);
        Assert.Null(svc.SnapshotFetchedUtc);

        t.Responses[PageUrl(0)] = Page0Body;
        t.Responses[PageUrl(1)] = EmptyPageBody;
        await svc.RefreshAsync(manual: true, NowUtc);

        Assert.Equal(NowUtc, svc.SnapshotFetchedUtc);
    }

    [Fact]
    public async Task SnapshotFetchedUtc_FlagTurnedOffAfterRefresh_ReturnsNull_EvenWithCachedData()
    {
        var (svc, t, settings) = Make(enabled: true);
        t.Responses[PageUrl(0)] = Page0Body;
        t.Responses[PageUrl(1)] = EmptyPageBody;
        await svc.RefreshAsync(manual: true, NowUtc);
        Assert.NotNull(svc.SnapshotFetchedUtc);

        settings.Current.MarketDataEnabled = false;   // toggled off later, same session, data still cached

        Assert.Null(svc.SnapshotFetchedUtc);
    }

    [Fact]
    public async Task Changed_FlagOff_NeverFires_OnStartOrRefresh()
    {
        var (svc, t, _) = Make(enabled: false);
        var fired = 0;
        svc.Changed += () => fired++;
        t.Responses[PageUrl(0)] = Page0Body;
        t.Responses[PageUrl(1)] = EmptyPageBody;

        svc.Start();
        await svc.RefreshAsync(manual: true, NowUtc);

        Assert.Equal(0, fired);
    }

    [Fact]
    public async Task Changed_FlagOn_FiresAfterRefresh()
    {
        var (svc, t, _) = Make(enabled: true);
        t.Responses[PageUrl(0)] = Page0Body;
        t.Responses[PageUrl(1)] = EmptyPageBody;
        var fired = 0;
        svc.Changed += () => fired++;

        await svc.RefreshAsync(manual: true, NowUtc);

        Assert.Equal(1, fired);
    }

    [Fact]
    public async Task Changed_FlagOn_FiresOnStart_WhenADiskSnapshotExistsFromAnEarlierRun()
    {
        var dir = Directory.CreateTempSubdirectory("nexus-sct-test").FullName;
        _tempDirs.Add(dir);
        var settingsPath = Path.Combine(dir, "settings.json");
        var snapshotPath = Path.Combine(dir, "sct_snapshot.json");

        // Earlier run: fetches and persists a snapshot to disk.
        var earlierSettings = new SettingsService(settingsPath);
        earlierSettings.Current.MarketDataEnabled = true;
        var earlierTransport = new FakeSctTransport();
        earlierTransport.Responses[PageUrl(0)] = Page0Body;
        earlierTransport.Responses[PageUrl(1)] = EmptyPageBody;
        var earlierSvc = new SctMarketService(earlierSettings, earlierTransport, snapshotPath);
        await earlierSvc.RefreshAsync(manual: true, NowUtc);

        // This run: a fresh instance over the same disk state, never itself fetched. Start()
        // loading the previous cycle's snapshot must still notify subscribers.
        var settings = new SettingsService(settingsPath);
        settings.Current.MarketDataEnabled = true;
        var svc = new SctMarketService(settings, new FakeSctTransport(), snapshotPath);
        var fired = 0;
        svc.Changed += () => fired++;

        svc.Start();

        Assert.Equal(1, fired);
        Assert.Equal(4, svc.Snapshot!.Rows.Count);
    }

    [Fact]
    public async Task Find_FlagOn_ResolvesKnownMappedRow()
    {
        var (svc, t, _) = Make(enabled: true);
        t.Responses[PageUrl(0)] = Page0Body;
        t.Responses[PageUrl(1)] = EmptyPageBody;
        await svc.RefreshAsync(manual: true, NowUtc);

        // Stanton > MIC L2 > MIC-L2 Long Forest Station = UEX terminal 55 (general trade);
        // Waste = UEX commodity 79 (both confirmed against Data/sct_uex_map.json).
        //
        // The fixture row is tagged SELLS, which is the SHOP selling - i.e. the PLAYER's BUY side.
        // This asked for "SELL" until 2026-08-03, which is what the inverted Find made pass.
        var found = svc.Find(terminalId: 55, commodityId: 79, side: "BUY");

        Assert.NotNull(found);
        Assert.Equal("waste", found!.Commodity);
        Assert.Equal(115, found.Price);
        Assert.Equal(1, found.Quantity);
    }

    [Fact]
    public async Task Find_WrongSide_ReturnsNull()
    {
        var (svc, t, _) = Make(enabled: true);
        t.Responses[PageUrl(0)] = Page0Body;
        t.Responses[PageUrl(1)] = EmptyPageBody;
        await svc.RefreshAsync(manual: true, NowUtc);

        // The only Waste row at this terminal is a SELLS, which serves the player's BUY side, so
        // the player's SELL side has nothing to match.
        Assert.Null(svc.Find(terminalId: 55, commodityId: 79, side: "SELL"));
    }

    // Regression pin for the side-polarity bug fixed 2026-08-03. SCT labels rows from the SHOP's
    // point of view and UEX from the PLAYER's, so the two vocabularies are mirrored: SELLS serves a
    // UEX buy, BUYS serves a UEX sell. Find compared the words directly and therefore paired every
    // row with its opposite. Against the shipped snapshots that produced 0 matches, where the
    // correct pairing produces 233 (buy) and 716 (sell) at a 0.0% median price delta - so nothing
    // was ever corroborated and no disagreement was ever raised.
    //
    // Both sides are asserted from ONE fixture holding both rows at the same terminal+commodity, so
    // a future inversion cannot pass by flipping a single expectation.
    [Fact]
    public async Task Find_MapsShopPerspectiveRowsToThePlayersSide()
    {
        const string bothSides = """
        {"content":[
          {"location":"stanton > mic l2 > mic-l2 long forest station","transaction":"SELLS","commodity":"waste","price":115,"quantity":1,"saturation":0.5,"boxSizesInScu":null,"batchId":"a1","timestamp":"2026-07-29T10:32:38-04:00"},
          {"location":"stanton > mic l2 > mic-l2 long forest station","transaction":"BUYS","commodity":"waste","price":88,"quantity":42,"saturation":0.5,"boxSizesInScu":null,"batchId":"a2","timestamp":"2026-07-29T10:32:38-04:00"}
        ],"page":{"size":100,"number":0,"totalElements":2,"totalPages":1}}
        """;
        var (svc, t, _) = Make(enabled: true);
        t.Responses[PageUrl(0)] = bothSides;
        t.Responses[PageUrl(1)] = EmptyPageBody;
        await svc.RefreshAsync(manual: true, NowUtc);

        // The shop SELLS to you at 115: that is what the player pays, the UEX buy side.
        var playerBuy = svc.Find(terminalId: 55, commodityId: 79, side: "BUY");
        Assert.NotNull(playerBuy);
        Assert.Equal(115, playerBuy!.Price);

        // The shop BUYS from you at 88: that is what the player receives, the UEX sell side.
        var playerSell = svc.Find(terminalId: 55, commodityId: 79, side: "SELL");
        Assert.NotNull(playerSell);
        Assert.Equal(88, playerSell!.Price);
    }

    [Fact]
    public async Task Find_UnmappedTerminalOrCommodityId_ReturnsNull()
    {
        var (svc, t, _) = Make(enabled: true);
        t.Responses[PageUrl(0)] = Page0Body;
        t.Responses[PageUrl(1)] = EmptyPageBody;
        await svc.RefreshAsync(manual: true, NowUtc);

        Assert.Null(svc.Find(terminalId: 999_999, commodityId: 79, side: "SELL"));
        Assert.Null(svc.Find(terminalId: 55, commodityId: 999_999, side: "SELL"));
    }

    [Fact]
    public async Task Find_FlagTurnedOffAfterRefresh_ReturnsNull_EvenWithCachedData()
    {
        var (svc, t, settings) = Make(enabled: true);
        t.Responses[PageUrl(0)] = Page0Body;
        t.Responses[PageUrl(1)] = EmptyPageBody;
        await svc.RefreshAsync(manual: true, NowUtc);
        // BUY, because the fixture row is a SELLS: the shop selling is the player buying. The side
        // is incidental to what this test is about (consent hides cached data), it just has to be
        // the one that actually resolves.
        Assert.NotNull(svc.Find(terminalId: 55, commodityId: 79, side: "BUY"));

        settings.Current.MarketDataEnabled = false;

        Assert.Null(svc.Find(terminalId: 55, commodityId: 79, side: "BUY"));
    }

    [Fact]
    public async Task SctOnlyBuyers_FlagOn_ReturnsOnlyBuysAtLocationsWithNoUexTerminalAtAll()
    {
        var (svc, t, _) = Make(enabled: true);
        t.Responses[PageUrl(0)] = SctOnlyLocationBody;
        t.Responses[PageUrl(1)] = EmptyPageBody;
        await svc.RefreshAsync(manual: true, NowUtc);

        var buyers = svc.SctOnlyBuyers(commodityId: 22);   // Corundum

        var buyer = Assert.Single(buyers);
        Assert.Equal("pyro > bloom > frigid knot", buyer.Location);
        Assert.Equal("BUYS", buyer.Transaction);
        Assert.Equal(3000, buyer.Price);
    }

    [Fact]
    public async Task SctOnlyBuyers_FlagTurnedOffAfterRefresh_ReturnsEmpty_EvenWithCachedData()
    {
        var (svc, t, settings) = Make(enabled: true);
        t.Responses[PageUrl(0)] = SctOnlyLocationBody;
        t.Responses[PageUrl(1)] = EmptyPageBody;
        await svc.RefreshAsync(manual: true, NowUtc);
        Assert.NotEmpty(svc.SctOnlyBuyers(commodityId: 22));

        settings.Current.MarketDataEnabled = false;

        Assert.Empty(svc.SctOnlyBuyers(commodityId: 22));
    }

    // --- 6h auto-refresh cadence (2026-07-30) ---------------------------------------------------
    // ── (a) ShouldFetch: the pure staleness gate ─────────────────────────────────────────────

    [Fact]
    public void ShouldFetch_NoSnapshot_IsDue()
    {
        Assert.True(SctMarketService.ShouldFetch(null, NowUtc));
    }

    [Fact]
    public void ShouldFetch_JustUnderSixHours_IsNotDue()
    {
        var fetched = NowUtc - TimeSpan.FromHours(5) - TimeSpan.FromMinutes(59);
        Assert.False(SctMarketService.ShouldFetch(fetched, NowUtc));
    }

    [Fact]
    public void ShouldFetch_ExactlySixHours_IsDue()
    {
        var fetched = NowUtc - SctMarketService.RefreshInterval;
        Assert.True(SctMarketService.ShouldFetch(fetched, NowUtc));
    }

    // A stamp in the FUTURE (a clock rollback, or data fetched while the clock was wrong) counts
    // as stale too: otherwise the subtraction stays negative and the auto path freezes forever
    // (MarketDataService.IsStale's own precedent).
    [Fact]
    public void ShouldFetch_FutureStamp_IsDue()
    {
        Assert.True(SctMarketService.ShouldFetch(NowUtc + TimeSpan.FromDays(3), NowUtc));
    }

    // ── (b) MaybeAutoRefresh / Start behavioral seams ────────────────────────────────────────

    [Fact]
    public void MaybeAutoRefresh_FlagOff_MakesNoRequests()
    {
        var (svc, t, _, _) = MakeWithPath(enabled: false);
        t.Responses[PageUrl(0)] = Page0Body;
        t.Responses[PageUrl(1)] = EmptyPageBody;

        svc.MaybeAutoRefresh();

        Assert.Empty(t.Requested);
    }

    [Fact]
    public void Start_FlagOff_KicksNothing_ZeroTransportCalls()
    {
        var (svc, t, _, _) = MakeWithPath(enabled: false);
        t.Responses[PageUrl(0)] = Page0Body;
        t.Responses[PageUrl(1)] = EmptyPageBody;

        svc.Start();

        // The timer is created regardless of the flag (so a later opt-in is picked up by the next
        // tick without a second Start() call - see the toggle-on test below), but the flag still
        // gates the disk load and the fetch-on-launch kick, so Start() itself makes zero requests.
        Assert.Empty(t.Requested);
    }

    // Closes the gap the review found: App.xaml.cs calls Start() exactly once, at launch, and
    // both toggle-on surfaces (SettingsPage, AdminPage) only ever call RefreshAsync(manual: true)
    // directly - neither one calls Start() again. So a user who launches with the flag off (the
    // default) and opts in later must be picked up by the timer's own tick, not by anything
    // Start() does at construction time. This drives the tick's entry point directly
    // (MaybeAutoRefresh, the existing test idiom for "simulate a tick" throughout this file)
    // rather than waiting on a real 6h DispatcherTimer.
    [Fact]
    public void Start_FlagOffThenToggledOn_TickPicksItUpWithoutASecondStartCall()
    {
        var (svc, t, settings, _) = MakeWithPath(enabled: false);
        t.Responses[PageUrl(0)] = Page0Body;
        t.Responses[PageUrl(1)] = EmptyPageBody;

        svc.Start();
        Assert.Empty(t.Requested);   // flag off at launch: Start() kicks nothing

        settings.Current.MarketDataEnabled = true;   // opted in later, e.g. via the Settings row
        svc.MaybeAutoRefresh();                   // the tick's own entry point, no second Start()

        Assert.True(SpinWait.SpinUntil(() => t.Requested.Count > 0, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void Start_FreshDiskCache_DoesNotFetch()
    {
        var (svc, t, _, snapshotPath) = MakeWithPath(enabled: true);
        var fresh = new SctSnapshot { FetchedUtc = DateTime.UtcNow, Rows = new List<SctListing>() };
        SctSnapshotFile.Save(snapshotPath, fresh);
        t.Responses[PageUrl(0)] = Page0Body;
        t.Responses[PageUrl(1)] = EmptyPageBody;

        svc.Start();

        // ShouldFetch is checked synchronously before the fire-and-forget Task.Run, so a fresh
        // cache never even reaches it: no race to spin-wait for here.
        Assert.Empty(t.Requested);
    }

    [Fact]
    public void Start_NoSnapshotOnDisk_KicksTheAutoRefresh()
    {
        var (svc, t, _, _) = MakeWithPath(enabled: true);
        t.Responses[PageUrl(0)] = Page0Body;
        t.Responses[PageUrl(1)] = EmptyPageBody;

        svc.Start();

        // Fire-and-forget (Task.Run), same timing note as MarketDataServiceTests' own
        // MaybeAutoRefresh_ForegroundRelevant_RunsNormally.
        Assert.True(SpinWait.SpinUntil(() => t.Requested.Count > 0, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void Start_StaleDiskCache_KicksTheAutoRefresh()
    {
        var (svc, t, _, snapshotPath) = MakeWithPath(enabled: true);
        var stale = new SctSnapshot { FetchedUtc = DateTime.UtcNow - TimeSpan.FromHours(7), Rows = new List<SctListing>() };
        SctSnapshotFile.Save(snapshotPath, stale);
        t.Responses[PageUrl(0)] = Page0Body;
        t.Responses[PageUrl(1)] = EmptyPageBody;

        svc.Start();

        Assert.True(SpinWait.SpinUntil(() => t.Requested.Count > 0, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void MaybeAutoRefresh_ForegroundNotRelevant_MakesNoRequests()
    {
        var (svc, t, _, _) = MakeWithPath(enabled: true, isForegroundRelevant: () => false);
        t.Responses[PageUrl(0)] = Page0Body;
        t.Responses[PageUrl(1)] = EmptyPageBody;

        svc.MaybeAutoRefresh();

        Assert.Empty(t.Requested);
    }

    [Fact]
    public void Start_ForegroundNotRelevant_DoesNotFetch_EvenWithNoSnapshotOnDisk()
    {
        var (svc, t, _, _) = MakeWithPath(enabled: true, isForegroundRelevant: () => false);
        t.Responses[PageUrl(0)] = Page0Body;
        t.Responses[PageUrl(1)] = EmptyPageBody;

        svc.Start();

        Assert.Empty(t.Requested);
    }

    [Fact]
    public void MaybeAutoRefresh_ForegroundRelevant_RunsWhenDue()
    {
        var (svc, t, _, _) = MakeWithPath(enabled: true, isForegroundRelevant: () => true);
        t.Responses[PageUrl(0)] = Page0Body;
        t.Responses[PageUrl(1)] = EmptyPageBody;

        svc.MaybeAutoRefresh();

        Assert.True(SpinWait.SpinUntil(() => t.Requested.Count > 0, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void MaybeAutoRefresh_NoForegroundFuncGiven_DefaultsToAlwaysRelevant()
    {
        var (svc, t, _, _) = MakeWithPath(enabled: true);   // no isForegroundRelevant passed
        t.Responses[PageUrl(0)] = Page0Body;
        t.Responses[PageUrl(1)] = EmptyPageBody;

        svc.MaybeAutoRefresh();

        Assert.True(SpinWait.SpinUntil(() => t.Requested.Count > 0, TimeSpan.FromSeconds(2)));
    }

    // Dispose must stop the timer as well as cancel any in-flight cycle: a late tick firing after
    // shutdown (simulated here by calling MaybeAutoRefresh directly) must still do nothing.
    [Fact]
    public void Dispose_StopsTheTimerAndIsIdempotent()
    {
        var (svc, t, _, snapshotPath) = MakeWithPath(enabled: true);
        var fresh = new SctSnapshot { FetchedUtc = DateTime.UtcNow, Rows = new List<SctListing>() };
        SctSnapshotFile.Save(snapshotPath, fresh);

        svc.Start();   // fresh cache: Start's own staleness check does not fire the timer's work early
        Assert.Empty(t.Requested);   // isolates the assertion below from Start's own auto-kick

        svc.Dispose();
        svc.Dispose();   // idempotent: must not throw

        t.Responses[PageUrl(0)] = Page0Body;
        t.Responses[PageUrl(1)] = EmptyPageBody;
        svc.MaybeAutoRefresh();   // simulates a late timer tick firing after shutdown

        Assert.Empty(t.Requested);
    }
}
