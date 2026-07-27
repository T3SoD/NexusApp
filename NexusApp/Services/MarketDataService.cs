using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace NexusApp.Services;

// Seam between the fetch cycle and the network, so every branch of the cycle is testable with a
// fake and the real HTTP code stays in one small class (the IUpdateTransport pattern).
internal interface IMarketDataTransport
{
    Task<string> GetStringAsync(string url, int maxBytes, CancellationToken ct);
}

internal sealed class HttpMarketTransport : IMarketDataTransport
{
    // One client for the process: a new HttpClient per call leaks sockets. 15s covers a slow
    // endpoint; the streamed body read below carries its own token because HttpClient.Timeout
    // does not govern reads under ResponseHeadersRead.
    private static readonly HttpClient _http = Create();

    private static HttpClient Create()
    {
        var handler = new SocketsHttpHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 5 };
        var c = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        // Identifies the app to UEX. Carries the app version only, nothing about the user.
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"NexusApp-Market/{NexusApp.AppInfo.Version}");
        return c;
    }

    public async Task<string> GetStringAsync(string url, int maxBytes, CancellationToken ct)
    {
        // Explicit time bound: a dribbling endpoint must not wedge the single-flight flag, and
        // the cycle's own deadline is linked in through ct.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
        EnsureHttps(resp);
        resp.EnsureSuccessStatusCode();
        if (resp.Content.Headers.ContentLength is { } len && len > maxBytes)
            throw new InvalidOperationException("response larger than expected");
        await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
        using var ms = new MemoryStream();
        await CopyCappedAsync(stream, ms, maxBytes, cts.Token).ConfigureAwait(false);
        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }

    // Defense in depth: even if the handler ever followed a downgrade redirect, the final
    // response must have arrived over https or it is discarded. The guarantee is ours,
    // not the framework's.
    private static void EnsureHttps(HttpResponseMessage resp)
    {
        if (resp.RequestMessage?.RequestUri?.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("response did not arrive over https");
    }

    // Copies at most maxBytes; one byte more aborts, so a lying or hostile endpoint cannot
    // flood memory. A Content-Length header is advisory and is not trusted on its own.
    private static async Task CopyCappedAsync(Stream from, Stream to, long maxBytes, CancellationToken ct)
    {
        var buf = new byte[81920];
        long total = 0;
        int n;
        while ((n = await from.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
        {
            total += n;
            if (total > maxBytes) throw new InvalidOperationException("response larger than expected");
            await to.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
        }
    }
}

// Owns the UEX fetch cycle: the hourly throttle, single-flight, the whole-cycle deadline, and
// the in-memory snapshot that the UI reads. The cycle is deliberately fault tolerant per
// DATASET: one endpoint failing never costs the others their data, because stale prices with a
// visible age are worth more to a miner than an empty panel.
public sealed class MarketDataService : IDisposable
{
    public const string Tag = "[NET]";
    public const string BaseUrl = "https://api.uexcorp.uk/2.0/";

    // The largest response (commodities_raw_prices_all) is a few hundred KB today; 8 MB leaves
    // room for growth while still capping a hostile or broken endpoint.
    public const int MaxResponseBytes = 8 * 1024 * 1024;

    public static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(1);

    // Yields and terminals are reference data that changes with a game patch, not with trading.
    public static readonly TimeSpan ReferenceInterval = TimeSpan.FromHours(24);

    // A cycle is about 30 sequential requests: five bulk endpoints plus one per distinct refined
    // parent id resolved from the 35-entry seed name map. Two minutes is a BACKSTOP against a
    // wedged endpoint, not a budget the cycle is expected to fit in comfortably; when it fires,
    // every dataset (and every refined id) that already completed is still published.
    public static readonly TimeSpan CycleDeadline = TimeSpan.FromMinutes(2);

    private const string GameVersionsEndpoint = "game_versions";
    private const string CommoditiesEndpoint = "commodities";
    private const string RawPricesEndpoint = "commodities_raw_prices_all";
    private const string RefinedPricesEndpoint = "commodities_prices";
    private const string YieldsEndpoint = "refineries_yields";
    private const string TerminalsEndpoint = "terminals";

    private readonly SettingsService _settings;
    private readonly IMarketDataTransport _transport;
    private readonly string _snapshotPath;
    private readonly bool _demo;

    // Cancelled by Dispose. Every cycle links its own deadline source to this one, so shutting
    // the service down also unwinds a cycle that is in flight.
    private readonly CancellationTokenSource _life = new();

    private int _busy;                          // Interlocked single-flight across the whole cycle
    // Completes when the in-flight cycle has finished ALL of its writes; null when idle. Dispose
    // waits on it so a cycle cannot write settings.json or the snapshot after App.OnExit.
    private volatile TaskCompletionSource<bool>? _cycleDone;
    private volatile MarketSnapshot? _snapshot;
    private volatile string? _lastError;
    private volatile bool _disposed;
    private bool _started;
    private System.Windows.Threading.DispatcherTimer? _timer;

    // The whole snapshot is swapped as one reference at the end of a cycle, never edited in
    // place, so a UI reader either sees the previous cycle's data or this cycle's, never a
    // half-updated mix. volatile makes that publication visible to other threads without a lock.
    internal MarketSnapshot? Snapshot => _snapshot;

    public bool FetchInProgress => Volatile.Read(ref _busy) != 0;

    // One short sentence naming the first thing that went wrong in the last cycle, for the
    // Settings status row. Null after a clean cycle. Never an exception dump.
    public string? LastError => _lastError;

    // Raised on a worker thread once per cycle, after the new snapshot is published; UI
    // subscribers marshal with Dispatcher.Invoke themselves (the UpdateService.Changed contract).
    public event Action? Changed;

    public MarketDataService(SettingsService settings)
        : this(settings, new HttpMarketTransport(),
               Path.Combine(AppPaths.Root, "cache", "uex_snapshot.json"), AppPaths.IsDemoProfile)
    { }

    internal MarketDataService(SettingsService settings, IMarketDataTransport transport, string snapshotPath,
                               bool isDemoProfile)
    {
        _settings = settings;
        _transport = transport;
        _snapshotPath = snapshotPath;
        _demo = isDemoProfile;
    }

    // Pure gate for the automatic path: consent must be an explicit yes, the demo profile is
    // always inert, and a snapshot fetched inside the last hour suppresses another cycle.
    internal static bool ShouldFetch(bool? enabled, bool isDemoProfile, DateTime newestFetchUtc, DateTime nowUtc)
    {
        if (isDemoProfile || enabled != true) return false;
        // Self-heal a stamp in the future (a clock rollback, or data fetched while the clock was
        // wrong): otherwise the subtraction stays negative and the auto refresh freezes forever.
        if (newestFetchUtc > nowUtc) return true;
        return nowUtc - newestFetchUtc >= RefreshInterval;
    }

    // Called once from app startup, on the UI thread. The timer is created HERE and not in the
    // constructor so tests (and any non-UI caller) can build the service without a dispatcher.
    public void Start()
    {
        if (_started || _disposed) return;
        _started = true;

        LoadSnapshotFromDisk();
        MaybeAutoRefresh();

        try
        {
            _timer = new System.Windows.Threading.DispatcherTimer { Interval = RefreshInterval };
            _timer.Tick += (_, _) => MaybeAutoRefresh();
            _timer.Start();
        }
        catch (Exception ex)
        {
            // A missing dispatcher costs the hourly tick, not the feature: manual refresh and the
            // snapshot already on disk both still work.
            Logger.Error($"{Tag} market refresh timer could not start", ex);
        }
    }

    // The disk half of Start, exposed as an internal seam so the load path is testable without
    // a dispatcher. A discarded file is an expected state (first run, schema bump), not an error.
    internal void LoadSnapshotFromDisk()
    {
        var loaded = MarketSnapshotFile.Load(_snapshotPath, out var reason);
        if (loaded is null)
        {
            Logger.Info($"{Tag} market snapshot not loaded: {reason ?? "no snapshot"}");
            return;
        }
        _snapshot = loaded;
        Logger.Info($"{Tag} market snapshot loaded: {loaded.Commodities.Rows.Count} commodities, " +
                    $"{loaded.RawPrices.Rows.Count} raw prices, {loaded.RefinedPrices.Rows.Count} refined prices");
        RaiseChanged();
    }

    // The auto path: the toggle, the demo profile, and the hourly throttle all gate it. Fire and
    // forget on the thread pool so a UI-thread caller (startup, timer tick) never blocks.
    public void MaybeAutoRefresh()
    {
        if (_disposed) return;
        var newest = _snapshot?.NewestFetchUtc ?? DateTime.MinValue;
        if (!ShouldFetch(_settings.Current.MarketDataEnabled, _demo, newest, DateTime.UtcNow)) return;

        _ = Task.Run(async () =>
        {
            try { await RefreshAsync(manual: false).ConfigureAwait(false); }
            catch (Exception ex) { Logger.Error($"{Tag} market auto refresh failed", ex); }
        });
    }

    // One fetch cycle. Runs regardless of the toggle when called directly: the Settings refresh
    // button is only reachable while market data is on, so the toggle gates the AUTO path only.
    public async Task RefreshAsync(bool manual)
    {
        if (_disposed) return;
        if (Interlocked.Exchange(ref _busy, 1) != 0) return;   // second caller returns without fetching

        var cycleRan = false;
        CancellationTokenSource? cts = null;
        // Published before any await so a Dispose racing this call always finds the cycle it
        // needs to wait for. Completed in the finally below, never faulted.
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _cycleDone = done;
        try
        {
            cts = CancellationTokenSource.CreateLinkedTokenSource(_life.Token);
            cts.CancelAfter(CycleDeadline);
            cycleRan = true;
            Logger.Info($"{Tag} market refresh started ({(manual ? "manual" : "auto")})");

            var utcNow = DateTime.UtcNow;
            // Start from a carbon copy of the live snapshot: every dataset that this cycle does
            // not successfully replace keeps its previous rows AND its previous FetchedUtc.
            var next = Carry(_snapshot);
            var cycle = new CycleResult();

            try
            {
                await RunCycleAsync(next, utcNow, cycle, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Deadline (or shutdown): whatever completed still lands below.
                cycle.Note(null, "the refresh ran out of time before it finished");
                Logger.Error($"{Tag} market refresh stopped: the cycle exceeded {CycleDeadline.TotalMinutes:0} minutes");
            }
            catch (Exception ex)
            {
                // The per-endpoint helpers already swallow their own failures, so reaching here
                // means something unforeseen. The partial snapshot is still worth publishing.
                cycle.Note(null, Shorten(ex.Message));
                Logger.Error($"{Tag} market refresh failed", ex);
            }

            // Error first, THEN the snapshot: the snapshot swap is what a reader notices, so
            // publishing it last guarantees nobody pairs this cycle's data with the previous
            // cycle's error text.
            _lastError = cycle.FirstError;
            _snapshot = next;                  // atomic publication of the whole cycle's result
            MarketSnapshotFile.Save(_snapshotPath, next);
            Logger.Info($"{Tag} market refresh finished: {cycle.Refreshed} datasets refreshed, {cycle.Failures} failed");
            RaiseChanged();
        }
        finally
        {
            // Stamped whether the cycle succeeded or not, so the Settings status line can say
            // when Nexus last TRIED. It does not drive the hourly throttle: ShouldFetch reads
            // the snapshot's own NewestFetchUtc, so a cycle where every endpoint failed is
            // retried on the next tick rather than being suppressed for an hour by this stamp.
            if (cycleRan)
            {
                _settings.Current.LastMarketFetchUtc = DateTime.UtcNow;
                _settings.Save();
            }
            cts?.Dispose();
            Interlocked.Exchange(ref _busy, 0);
            // Last: Dispose waits on this, and everything it must not race (the snapshot file
            // and settings writes above) is finished by the time it completes.
            _cycleDone = null;
            done.TrySetResult(true);
        }
    }

    private async Task RunCycleAsync(MarketSnapshot next, DateTime utcNow, CycleResult cycle, CancellationToken ct)
    {
        // 1. Live game version: labels the prices in the UI ("patch 4.9"). A failure keeps the
        //    previous label rather than blanking it.
        var (body, ms) = await FetchAsync(GameVersionsEndpoint, BaseUrl + GameVersionsEndpoint, cycle, ct).ConfigureAwait(false);
        if (body is not null)
        {
            var live = MarketParse.ParseLiveGameVersion(body);
            if (live is not null)
            {
                next.LiveGameVersion = live;
                Logger.Info($"{Tag} market fetch {GameVersionsEndpoint}: live {live} ({Bytes(body)} bytes, {ms}ms)");
            }
            else
            {
                NoteShape(cycle, GameVersionsEndpoint);
            }
        }

        // 2. Commodity catalogue: also the input to the refined leg below.
        (body, ms) = await FetchAsync(CommoditiesEndpoint, BaseUrl + CommoditiesEndpoint, cycle, ct).ConfigureAwait(false);
        if (body is not null && RequireArray(body, CommoditiesEndpoint, cycle))
        {
            var rows = MarketParse.ParseCommodities(body, out var skipped);
            LogRows(CommoditiesEndpoint, rows.Count, skipped, body, ms);
            if (rows.Count > 0)
            {
                next.Commodities = new MarketDataset<MarketCommodity> { FetchedUtc = utcNow, Rows = rows };
                cycle.Refreshed++;
            }
            else
            {
                KeptOnEmpty(CommoditiesEndpoint);
            }
        }

        // 3. Raw ore prices, every terminal in one call.
        (body, ms) = await FetchAsync(RawPricesEndpoint, BaseUrl + RawPricesEndpoint, cycle, ct).ConfigureAwait(false);
        if (body is not null && RequireArray(body, RawPricesEndpoint, cycle))
        {
            var rows = MarketParse.ParsePriceRows(body, out var skipped);
            LogRows(RawPricesEndpoint, rows.Count, skipped, body, ms);
            if (rows.Count > 0)
            {
                next.RawPrices = new MarketDataset<MarketPriceRow> { FetchedUtc = utcNow, Rows = rows };
                cycle.Refreshed++;
            }
            else
            {
                KeptOnEmpty(RawPricesEndpoint);
            }
        }

        // 4. Refined prices: no bulk endpoint exists, so this is a union of one call per refined
        //    commodity the seed data actually cares about.
        await FetchRefinedAsync(next, utcNow, cycle, ct).ConfigureAwait(false);

        // 5. Reference data, on its own much slower clock.
        if (utcNow - next.Yields.FetchedUtc >= ReferenceInterval)
        {
            (body, ms) = await FetchAsync(YieldsEndpoint, BaseUrl + YieldsEndpoint, cycle, ct).ConfigureAwait(false);
            if (body is not null && RequireArray(body, YieldsEndpoint, cycle))
            {
                var rows = MarketParse.ParseYieldRows(body, out var skipped);
                LogRows(YieldsEndpoint, rows.Count, skipped, body, ms);
                if (rows.Count > 0)
                {
                    next.Yields = new MarketDataset<MarketYieldRow> { FetchedUtc = utcNow, Rows = rows };
                    cycle.Refreshed++;
                }
                else
                {
                    KeptOnEmpty(YieldsEndpoint);
                }
            }
        }

        if (utcNow - next.Terminals.FetchedUtc >= ReferenceInterval)
        {
            (body, ms) = await FetchAsync(TerminalsEndpoint, BaseUrl + TerminalsEndpoint, cycle, ct).ConfigureAwait(false);
            if (body is not null && RequireArray(body, TerminalsEndpoint, cycle))
            {
                var rows = MarketParse.ParseTerminals(body, out var skipped);
                LogRows(TerminalsEndpoint, rows.Count, skipped, body, ms);
                if (rows.Count > 0)
                {
                    next.Terminals = new MarketDataset<MarketTerminal> { FetchedUtc = utcNow, Rows = rows };
                    cycle.Refreshed++;
                }
                else
                {
                    KeptOnEmpty(TerminalsEndpoint);
                }
            }
        }
    }

    // The refined dataset is the union of per-commodity fetches, so it merges instead of
    // replacing: an id that failed keeps ONLY its own previous rows, every id that succeeded
    // replaces its own. Ids that are no longer mapped drop out, which is how a commodity
    // renamed or removed by a patch stops haunting the dataset.
    private async Task FetchRefinedAsync(MarketSnapshot next, DateTime utcNow, CycleResult cycle, CancellationToken ct)
    {
        var ids = RefinedIdsFor(next.Commodities.Rows);
        if (ids.Count == 0)
        {
            // No commodity catalogue yet (first run with a failed step 2): leave the previous
            // refined rows exactly as they are rather than wiping them.
            Logger.Info($"{Tag} market fetch {RefinedPricesEndpoint} skipped: no mapped refined commodities yet");
            return;
        }

        var fresh = new List<MarketPriceRow>();
        var replaced = new HashSet<int>();
        try
        {
            foreach (var id in ids)
            {
                var endpoint = $"{RefinedPricesEndpoint} id {id}";
                var (body, ms) = await FetchAsync(endpoint, $"{BaseUrl}{RefinedPricesEndpoint}?id_commodity={id}", cycle, ct)
                    .ConfigureAwait(false);
                if (body is null || !RequireArray(body, endpoint, cycle)) continue;

                var rows = MarketParse.ParsePriceRows(body, out var skipped);
                LogRows(endpoint, rows.Count, skipped, body, ms);
                if (rows.Count == 0)
                {
                    KeptOnEmpty(endpoint);
                    continue;
                }
                replaced.Add(id);
                fresh.AddRange(rows);
            }
        }
        finally
        {
            // The merge runs on the way out no matter how the loop ends. This leg is ~30 of the
            // cycle's requests, so the deadline (or a shutdown) lands INSIDE it far more often
            // than anywhere else, and unwinding without merging would throw away every id that
            // already came back. Pure list work, so it cannot throw over the original exception.
            MergeRefined(next, utcNow, cycle, ids, fresh, replaced);
        }
    }

    // Union semantics, applied to whatever the leg managed to fetch: ids that returned rows
    // replace their own rows, ids that failed (or never ran, because the cycle was cancelled
    // first) keep their own previous rows, and ids no longer mapped drop out entirely.
    private static void MergeRefined(MarketSnapshot next, DateTime utcNow, CycleResult cycle, List<int> ids,
                                     List<MarketPriceRow> fresh, HashSet<int> replaced)
    {
        if (replaced.Count == 0) return;   // nothing landed: the previous rows and stamp stand

        var wanted = new HashSet<int>(ids);
        var merged = new List<MarketPriceRow>(fresh);
        foreach (var row in next.RefinedPrices.Rows)
        {
            if (wanted.Contains(row.CommodityId) && !replaced.Contains(row.CommodityId)) merged.Add(row);
        }
        next.RefinedPrices = new MarketDataset<MarketPriceRow> { FetchedUtc = utcNow, Rows = merged };
        cycle.Refreshed++;
    }

    // Seed resource -> UEX raw commodity (by name) -> its refined parent. Distinct, because
    // several raw ores can share a refined parent.
    private static List<int> RefinedIdsFor(List<MarketCommodity> commodities)
    {
        var ids = new List<int>();
        if (commodities.Count == 0) return ids;

        var byName = new Dictionary<string, MarketCommodity>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in commodities) byName.TryAdd(c.Name, c);

        var seen = new HashSet<int>();
        foreach (var uexRawName in MarketNameMap.SeedToUexRaw.Values)
        {
            if (!byName.TryGetValue(uexRawName, out var raw)) continue;
            var refined = MarketNameMap.RefinedFor(raw, commodities);
            if (refined is null || refined.Id <= 0) continue;
            if (seen.Add(refined.Id)) ids.Add(refined.Id);
        }
        return ids;
    }

    // Returns the body, or null when the request failed (already noted and logged). A cancelled
    // cycle rethrows so the remaining endpoints are skipped instead of failing one by one.
    private async Task<(string? body, long ms)> FetchAsync(string endpoint, string url, CycleResult cycle, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var body = await _transport.GetStringAsync(url, MaxResponseBytes, ct).ConfigureAwait(false);
            return (body, sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // ex.Message only: a stack trace in nexus.log for a routine network blip is noise,
            // and nothing from a response body is ever echoed. A per-request timeout arrives as
            // TaskCanceledException, whose message ("A task was canceled") would tell the user
            // nothing, so it gets named properly.
            var reason = ex is OperationCanceledException ? "the request timed out" : Shorten(ex.Message);
            Logger.Error($"{Tag} market fetch {endpoint} failed: {reason}");
            cycle.Note(endpoint, reason);
            return (null, sw.ElapsedMilliseconds);
        }
    }

    // A 200 carrying an error envelope, an HTML captive-portal page, or a non-array payload is a
    // failure, not an empty dataset: the difference decides whether previous rows are kept.
    private static bool RequireArray(string body, string endpoint, CycleResult cycle)
    {
        if (MarketParse.TryGetData(body, out var data) && data.ValueKind == JsonValueKind.Array) return true;
        NoteShape(cycle, endpoint);
        return false;
    }

    private static void NoteShape(CycleResult cycle, string endpoint)
    {
        Logger.Error($"{Tag} market fetch {endpoint} failed: the response was not in the expected format");
        cycle.Note(endpoint, "the response was not in the expected format");
    }

    // A well formed but empty array is not an error (it is how UEX reports "nothing listed"),
    // but it never replaces rows either: stale prices with a visible age beat an empty panel.
    private static void KeptOnEmpty(string endpoint) =>
        Logger.Info($"{Tag} market fetch {endpoint} returned no rows; keeping the previous rows");

    private static void LogRows(string endpoint, int kept, int skipped, string body, long ms) =>
        Logger.Info($"{Tag} market fetch {endpoint}: {kept} rows ({skipped} skipped, {Bytes(body)} bytes, {ms}ms)");

    private static int Bytes(string body) => Encoding.UTF8.GetByteCount(body);

    // Keeps LastError to one readable line: the Settings status row renders it inline.
    private static string Shorten(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return "unknown error";
        var line = message.Split('\n')[0].Trim();
        return line.Length <= 120 ? line : line[..120];
    }

    // A shallow copy: the datasets are new objects (so replacing one cannot be seen by a reader
    // holding the previous snapshot) but the Rows lists are shared by reference, which is safe
    // because a list is never mutated once published. Every replacement builds a new list.
    private static MarketSnapshot Carry(MarketSnapshot? previous) => new()
    {
        Schema = 1,
        LiveGameVersion = previous?.LiveGameVersion ?? "",
        Commodities = CopyDataset(previous?.Commodities),
        RawPrices = CopyDataset(previous?.RawPrices),
        RefinedPrices = CopyDataset(previous?.RefinedPrices),
        Yields = CopyDataset(previous?.Yields),
        Terminals = CopyDataset(previous?.Terminals),
    };

    private static MarketDataset<T> CopyDataset<T>(MarketDataset<T>? dataset) =>
        dataset is null ? new MarketDataset<T>()
                        : new MarketDataset<T> { FetchedUtc = dataset.FetchedUtc, Rows = dataset.Rows };

    // A subscriber must never be able to fault the fetch cycle (fail-closed). The real case is a
    // UI handler calling Dispatcher.Invoke while the app is shutting down.
    private void RaiseChanged()
    {
        try { Changed?.Invoke(); }
        catch (Exception ex) { Logger.Error($"{Tag} a market data subscriber threw", ex); }
    }

    // How long Dispose waits for a cancelled cycle to finish unwinding. Long enough for an
    // aborted request to throw and the snapshot save to complete, short enough that shutdown
    // never visibly stalls.
    private static readonly TimeSpan DisposeDrainTimeout = TimeSpan.FromSeconds(3);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Stop must run on the timer's own dispatcher thread; on shutdown from anywhere else it
        // throws, and a failed stop on a service that is going away is not worth an error.
        try { _timer?.Stop(); } catch { /* best effort */ }
        _timer = null;

        var pending = _cycleDone?.Task;   // captured BEFORE the cancel, which may clear the field
        // Cancels the in-flight cycle through its linked source. _life is deliberately NOT
        // disposed: that cycle still holds a token derived from it.
        try { _life.Cancel(); } catch { /* best effort */ }

        // Then WAIT for it. Cancelling alone is not enough: the cycle still publishes what
        // landed and writes settings.json and the snapshot file on its way out, and those
        // writes must not race App.OnExit (which saves settings itself and then relaunches for
        // a portable update). Bounded, because a subscriber that blocks inside Changed must not
        // be able to hang shutdown: a Changed handler should marshal with BeginInvoke, since a
        // blocking Dispatcher.Invoke while the UI thread sits here costs the full timeout.
        try
        {
            if (pending is not null && !pending.Wait(DisposeDrainTimeout))
                Logger.Error($"{Tag} market refresh did not stop within {DisposeDrainTimeout.TotalSeconds:0}s; leaving it to finish");
        }
        catch (Exception ex) { Logger.Error($"{Tag} market refresh did not stop cleanly: {Shorten(ex.Message)}"); }
    }

    // What one cycle did, so the end-of-cycle log line and LastError read from the same record.
    private sealed class CycleResult
    {
        public string? FirstError { get; private set; }
        public int Refreshed { get; set; }
        public int Failures { get; private set; }

        // The FIRST failure is the one the user sees: it is usually the cause, and the ones
        // after it are usually the same outage repeating.
        public void Note(string? endpoint, string reason)
        {
            Failures++;
            FirstError ??= endpoint is null ? reason : $"{endpoint} failed: {reason}";
        }
    }
}
