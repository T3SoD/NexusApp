using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace NexusApp.Services;

// Seam between the fetch cycle and the network (IMarketDataTransport's own pattern), so the whole
// cycle is testable with a fake and the real HTTP code stays in one small class.
internal interface ISctTransport
{
    Task<string> GetStringAsync(string url, int maxBytes, CancellationToken ct);
}

internal sealed class HttpSctTransport : ISctTransport
{
    private static readonly HttpClient _http = Create();

    private static HttpClient Create()
    {
        var handler = new SocketsHttpHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 5 };
        var c = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"NexusApp-Sct/{NexusApp.AppInfo.Version}");
        return c;
    }

    public async Task<string> GetStringAsync(string url, int maxBytes, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
        if (resp.RequestMessage?.RequestUri?.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("response did not arrive over https");
        resp.EnsureSuccessStatusCode();
        if (resp.Content.Headers.ContentLength is { } len && len > maxBytes)
            throw new InvalidOperationException("response larger than expected");
        await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
        using var ms = new MemoryStream();
        var buf = new byte[81920];
        long total = 0;
        int n;
        while ((n = await stream.ReadAsync(buf, cts.Token).ConfigureAwait(false)) > 0)
        {
            total += n;
            if (total > maxBytes) throw new InvalidOperationException("response larger than expected");
            await ms.WriteAsync(buf.AsMemory(0, n), cts.Token).ConfigureAwait(false);
        }
        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }
}

// The SCT (SC Trade Tools) crowdsource-listings cache. FULLY INERT while
// AppSettings.MarketDataEnabled is not true: every public entry point (Start, RefreshAsync,
// SnapshotFetchedUtc, Find, SctOnlyBuyers) checks the flag FIRST and returns null/empty/no-ops
// before touching the network, the embedded map, or disk - live-checked on every call, not just
// "never populated while off," so declining market data after data was already cached hides it
// again immediately.
//
// ONE CONSENT, ONE CLOCK (2026-08-03: one toggle and one refresh timer cover both feeds,
// all or nothing). This used to carry its own AppSettings.SctDataEnabled toggle, shown in both
// Settings and the Admin card, and its own 6h DispatcherTimer. Both are gone: live market data is
// now a single yes/no covering both feeds, and this one rides MarketDataService's tick. What did
// NOT change is RefreshInterval - see Start().
//
// See docs/superpowers/specs/2026-07-29-trade-api-recon-uex.md for the original recon.
public sealed class SctMarketService : IDisposable
{
    public const string Tag = "[NET]";

    // Base URL confirmed live against the real endpoint 2026-07-29.
    public const string BaseUrl = "https://sc-trade.tools/api/";
    private const string ListingsEndpoint = "crowdsource/commodity-listings";

    public const int MaxResponseBytes = 512 * 1024;   // one ~100-row page, generous headroom

    // The raw feed is a LEDGER (median age 38 days, divergence benchmark): anything older than
    // this is dropped at ingest, never surfaced as if it were current.
    public static readonly TimeSpan MaxListingAge = TimeSpan.FromDays(7);

    // Politeness pacing between page requests (spec: "polite 150ms spacing"), and the whole-cycle
    // backstop. 300ish observed pages at 150ms alone is ~45s; 10 minutes leaves generous room for
    // real request latency without ever feeling like a silent hang if the endpoint is slow.
    public static readonly TimeSpan PageSpacing = TimeSpan.FromMilliseconds(150);
    public static readonly TimeSpan FetchDeadline = TimeSpan.FromMinutes(10);
    private const int MaxPages = 400;   // safety cap above the observed ~300

    // Auto-refresh cadence (2026-07-30): the crowdsource feed is a slow-moving ledger (median
    // listing age 38 days), so there is no value chasing MarketDataService's hourly cadence -
    // 6h keeps the cache reasonably current without hammering the endpoint on every launch.
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(6);

    private readonly SettingsService _settings;
    private readonly ISctTransport _transport;
    private readonly string _snapshotPath;
    private readonly Func<bool>? _isForegroundRelevant;
    private readonly CancellationTokenSource _life = new();

    private int _busy;
    // Completes when the in-flight refresh has finished its snapshot-file write; null when idle.
    // Dispose waits on it so a refresh cannot write the snapshot file after App.OnExit has moved
    // on (the same MarketDataService._cycleDone contract).
    private volatile TaskCompletionSource<bool>? _cycleDone;
    private volatile SctSnapshot? _snapshot;
    private volatile bool _disposed;
    private bool _started;

    // Cached once, lazily, and only from a code path already gated by the flag check (never
    // touched while market data is off - "zero map load while off" applies here too, not
    // just to RefreshAsync's own load). A benign race (two callers both building it at once) is
    // acceptable: the embedded resource never changes at runtime, so both builds are identical.
    private volatile SctMapIndex? _mapIndex;

    internal SctSnapshot? Snapshot => _snapshot;
    public bool FetchInProgress => Volatile.Read(ref _busy) != 0;

    // Null while the flag is off, OR while it is on but no cycle (this run or a previous one, via
    // Start()) has ever produced a snapshot yet. Read live on every access, matching Find/
    // SctOnlyBuyers below - not cached at construction time.
    public DateTime? SnapshotFetchedUtc =>
        _settings.Current.MarketDataEnabled == true ? _snapshot?.FetchedUtc : null;

    // Raised on a worker thread (RefreshAsync) or the caller's own thread (Start's disk load)
    // after a new snapshot is published; UI subscribers marshal with Dispatcher.Invoke themselves
    // (the MarketDataService.Changed contract). Both call sites that raise it are themselves only
    // reachable when the flag is on, so this never fires while market data is off.
    public event Action? Changed;

    public SctMarketService(SettingsService settings, Func<bool>? isForegroundRelevant = null)
        : this(settings, new HttpSctTransport(), Path.Combine(AppPaths.Root, "cache", "sct_snapshot.json"),
               isForegroundRelevant)
    { }

    internal SctMarketService(SettingsService settings, ISctTransport transport, string snapshotPath,
                              Func<bool>? isForegroundRelevant = null)
    {
        _settings = settings;
        _transport = transport;
        _snapshotPath = snapshotPath;
        _isForegroundRelevant = isForegroundRelevant;
    }

    // Pure gate for the automatic path (MarketDataService.ShouldFetch's own idiom, collapsed to
    // one timestamp because SCT has exactly one dataset rather than several independently-stamped
    // ones). Null (no snapshot yet, this run or ever) is due immediately. A stamp in the future (a
    // clock rollback, or data fetched while the clock was wrong) counts as stale too, so the auto
    // path cannot freeze forever behind a negative subtraction.
    internal static bool ShouldFetch(DateTime? snapshotFetchedUtc, DateTime nowUtc)
    {
        if (snapshotFetchedUtc is null) return true;
        var fetchedUtc = snapshotFetchedUtc.Value;
        return fetchedUtc > nowUtc || nowUtc - fetchedUtc >= RefreshInterval;
    }

    // Called once from app startup, on the UI thread, and never again: App.xaml.cs calls this
    // exactly once. The timer is therefore created UNCONDITIONALLY, before the flag check below
    // (MarketDataService.Start's own shape) - the flag can be turned on later purely through the
    // Settings/Admin toggle, with no second Start() call ever happening, so the periodic tick is
    // the ONLY mechanism that ever notices a later opt-in. A tick that fires while the flag is
    // off is completely inert: MaybeAutoRefresh checks the flag first and returns silently,
    // logging nothing, so the timer's existence carries no observable behavior while the flag is
    // off. The flag check further down still gates the disk load and the fetch-on-launch kick -
    // fully skipped while the flag is off, so a user who tried this once and turned it back off
    // carries no residual disk read on every later launch. The timer is created HERE and not in
    // the constructor so tests (and any non-UI caller) can build the service without a dispatcher
    // (MarketDataService.Start's own reasoning).
    public void Start()
    {
        if (_started || _disposed) return;
        _started = true;

        // No timer of its own any more. This feed rides MarketDataService's hourly tick
        // (App wires AutoRefreshTick), so the app has ONE market clock and ONE consent
        // (2026-08-03: one toggle and one refresh timer cover both feeds, all or nothing).
        //
        // RefreshInterval still applies and is still 6h: ShouldAutoRefresh gates every attempt on
        // it, so being CHECKED hourly does not mean being FETCHED hourly. That spacing is
        // deliberate and must not be shortened to match the market cadence - an SCT refresh is a
        // paged crawl (PageSpacing, FetchDeadline) against endpoints approved for in-app use, and
        // hammering it hourly would abuse that approval.
        //
        // The launch kick stays at the BOTTOM of this method, after the disk load. Calling it up
        // here instead makes the staleness check read a still-null snapshot and fetch on every
        // launch even with a fresh cache on disk.

        if (_settings.Current.MarketDataEnabled != true)
        {
            Logger.Info($"{Tag} sct: dark flag off, not starting");
            return;
        }

        var loaded = SctSnapshotFile.Load(_snapshotPath, out var reason);
        if (loaded is null)
        {
            Logger.Info($"{Tag} sct snapshot not loaded: {reason ?? "no snapshot"}");
        }
        else
        {
            _snapshot = loaded;
            Logger.Info($"{Tag} sct snapshot loaded: {loaded.Rows.Count} listing(s), fetched {loaded.FetchedUtc:yyyy-MM-dd HH:mm} UTC");
            RaiseChanged();
        }

        // Fetch-on-launch-when-stale (2026-07-30): the same 6h staleness check every market tick
        // now applies, so a fresh disk cache never triggers a network call on startup, but a
        // missing or 6h+ old one is topped up right away instead of waiting for the first tick.
        MaybeAutoRefresh();
    }

    // The auto path: the flag, foreground relevance, and the 6h staleness check all gate it. Fire
    // and forget on the thread pool so a UI-thread caller (Start, timer tick) never blocks
    // (MarketDataService.MaybeAutoRefresh's own pattern).
    public void MaybeAutoRefresh()
    {
        if (_disposed) return;
        if (_settings.Current.MarketDataEnabled != true) return;   // every public entry point checks first

        // Trading tab (2026-07-30): no background polling while neither Nexus nor Star Citizen
        // has focus. Reuses the existing foreground facility (App.IsForegroundRelevant) the same
        // way MarketDataService does; null (no func given, e.g. every existing construction site
        // and every existing test) means "always relevant," so this is a no-op change for callers
        // that never opt in.
        if (_isForegroundRelevant is not null && !_isForegroundRelevant())
        {
            Logger.Info($"{Tag} sct auto refresh skipped: Nexus/Star Citizen not in the foreground");
            return;
        }

        // Read the snapshot once: it can be swapped by a cycle finishing on another thread.
        if (!ShouldFetch(_snapshot?.FetchedUtc, DateTime.UtcNow)) return;

        _ = Task.Run(async () =>
        {
            try { await RefreshAsync(manual: false).ConfigureAwait(false); }
            catch (Exception ex) { Logger.Error($"{Tag} sct auto refresh failed", ex); }
        });
    }

    // The one fetch cycle: page the crowdsource-listings endpoint, cut anything older than
    // MaxListingAge, drop anything the map cannot resolve to a known SCT shop path, cache the
    // survivors. nowUtc defaults to DateTime.UtcNow in production; tests pin it explicitly so the
    // freshness cutoff is deterministic rather than a function of whatever day this suite happens
    // to run (the same reasoning as Logger.WriteTo's testable nowUtc parameter).
    public async Task RefreshAsync(bool manual, DateTime? nowUtc = null)
    {
        if (_settings.Current.MarketDataEnabled != true) return;   // every public entry point checks first
        if (Interlocked.Exchange(ref _busy, 1) != 0) return;    // single-flight

        // Published before any await so a Dispose racing this call always finds the cycle it
        // needs to wait for. Completed in the finally below, never faulted.
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _cycleDone = done;
        try
        {
            Logger.Info($"{Tag} sct refresh started ({(manual ? "manual" : "auto")})");
            var utcNow = nowUtc ?? DateTime.UtcNow;

            // Late disk hydration (review fix, 2026-08-01): a RUNTIME opt-in reaches here with
            // _snapshot null even when a good cache sits on disk, because Start's disk load is
            // flag-gated and runs once at launch (off at launch + toggled on later = never
            // loaded). Without this, the zero-rows guard below had nothing to protect and an
            // endpoint hiccup could overwrite the good on-disk cache with a fresh-stamped empty
            // snapshot - the exact wipe the guard exists to prevent. Publishing the disk rows
            // first also puts cached data on screen immediately instead of after the full fetch.
            if (_snapshot is null)
            {
                var disk = SctSnapshotFile.Load(_snapshotPath, out _);
                if (disk is not null)
                {
                    _snapshot = disk;
                    Logger.Info($"{Tag} sct snapshot loaded: {disk.Rows.Count} listing(s), fetched {disk.FetchedUtc:yyyy-MM-dd HH:mm} UTC");
                    RaiseChanged();
                }
            }

            // Map load happens ONLY here, after the flag check above - "zero map load while off".
            var map = EnsureMapIndex().Map;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(_life.Token);
            cts.CancelAfter(FetchDeadline);

            var all = new List<SctListing>();
            int totalSkippedRows = 0, page = 0;
            // A cycle that did not page cleanly to its natural end publishes NOTHING: the same rule
            // MarketDataService's Carry applies (every dataset a cycle failed to replace keeps its
            // previous rows and its previous FetchedUtc). Without this, a transient failure - or
            // Dispose's own cancellation - replaced a good in-memory AND on-disk snapshot with an
            // empty one stamped fresh, which reads to every consumer as "SCT genuinely has nothing".
            bool cycleOk = true;
            while (page < MaxPages)
            {
                string body;
                try
                {
                    // Polite spacing between consecutive requests, INSIDE the guarded try (review
                    // fix): a cancel used to land in an unguarded trailing delay - the widest
                    // window of the whole loop - and escape RefreshAsync entirely, so a clean
                    // shutdown logged as "[UI] ...: SCT fetch failed" with a stack trace. Leading
                    // placement also skips the old pointless wait after the final page.
                    if (page > 0)
                        await Task.Delay(PageSpacing, cts.Token).ConfigureAwait(false);
                    body = await _transport.GetStringAsync($"{BaseUrl}{ListingsEndpoint}?page={page}",
                        MaxResponseBytes, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Three distinct producers reach this catch; naming the wrong one sent a
                    // reader hunting a 10-minute hang that was actually a normal exit (or a
                    // single slow request). Order matters: the deadline token is linked to
                    // _life, so shutdown must be ruled out first.
                    Logger.Error(
                        _life.IsCancellationRequested
                            ? $"{Tag} sct refresh stopped: cancelled by shutdown at page {page}"
                        : cts.IsCancellationRequested
                            ? $"{Tag} sct refresh stopped: deadline ({FetchDeadline.TotalMinutes:0}m) reached at page {page}"
                            : $"{Tag} sct refresh stopped: page {page} request timed out");
                    cycleOk = false;
                    break;
                }
                catch (Exception ex)
                {
                    Logger.Error($"{Tag} sct fetch page {page} failed: {ex.Message}");
                    cycleOk = false;
                    break;
                }

                var rows = SctListingParser.Parse(body, out var skipped);
                totalSkippedRows += skipped;
                if (rows.Count == 0) break;   // last page (or an empty/malformed one): stop paging

                all.AddRange(rows);
                page++;
            }

            if (!cycleOk)
            {
                // The failure itself is already logged above (deadline or page error); this line is
                // what tells the App Log Monitor that the previous data is still what the UI shows.
                Logger.Info($"{Tag} sct refresh incomplete after {page} page(s): previous snapshot kept");
                return;
            }

            if (all.Count == 0 && _snapshot is { } prev && prev.Rows.Count > 0)
            {
                // Page 0 answered 200 with zero rows. The real feed pages ~300 deep on a ledger
                // whose median listing age is 38 days, so a literal global zero is an endpoint
                // hiccup (empty body behind a healthy status), not a real "SCT has nothing" -
                // and publishing it would replace a good snapshot in memory AND on disk with an
                // empty one stamped fresh. Same preserve rule as the cycleOk branch above. A
                // first-ever fetch has no snapshot worth protecting and still publishes, keeping
                // the honest "nothing cached yet" state stamped.
                Logger.Info($"{Tag} sct refresh returned 0 row(s): previous snapshot kept");
                return;
            }

            var fresh = SctListingParser.Fresh(all, MaxListingAge, utcNow);

            // SCT is user-submitted and unvalidated, so a mistyped price arrives looking exactly
            // like a real one and - because a lookup takes the newest listing per terminal - one
            // typo becomes the number on screen. Rejected at ingest so the cached snapshot never
            // holds it. See SctOutlierFilter for the measurement behind the threshold.
            var (plausible, droppedOutliers) = SctOutlierFilter.Apply(fresh);
            if (droppedOutliers > 0)
                Logger.Info($"{Tag} sct: {droppedOutliers} implausible price(s) dropped " +
                            $"(beyond {SctOutlierFilter.Multiple:0}x their commodity median)");

            var kept = new List<SctListing>();
            int droppedUnmapped = 0;
            foreach (var r in plausible)
            {
                if (map.Terminals.ContainsKey(r.Location)) kept.Add(r);
                else droppedUnmapped++;
            }
            if (droppedUnmapped > 0)
                Logger.Info($"{Tag} sct join: {droppedUnmapped} listing(s) at unmapped locations dropped");

            _snapshot = new SctSnapshot { FetchedUtc = utcNow, Rows = kept };
            SctSnapshotFile.Save(_snapshotPath, _snapshot);
            Logger.Info($"{Tag} sct refresh finished: {page} page(s), {all.Count} raw row(s) " +
                        $"({totalSkippedRows} row(s) skipped), {fresh.Count} within {MaxListingAge.TotalDays:0}d, " +
                        $"{droppedOutliers} implausible, " +
                        $"{kept.Count} kept, {droppedUnmapped} unmapped");
            RaiseChanged();
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
            // Last: Dispose waits on this, and the snapshot file write above is finished by the
            // time it completes.
            _cycleDone = null;
            done.TrySetResult(true);
        }
    }

    // Corroboration lookup for one UEX-keyed price row (dossier/work-order/decoder callers speak
    // in terminalId/commodityId, same identity TradePriceRow uses - never in SCT's own free-text
    // location/commodity names). side accepts "BUY"/"BUYS" or "SELL"/"SELLS" case-insensitively,
    // matched against the row's own SCT transaction string. Returns the freshest kept listing at
    // that terminal+commodity+side, or null when the flag is off, nothing has been fetched yet,
    // either id is unknown to the map, or no kept row matches.
    public SctListing? Find(int terminalId, int commodityId, string side)
    {
        if (_settings.Current.MarketDataEnabled != true) return null;
        var snap = _snapshot;
        if (snap is null || snap.Rows.Count == 0) return null;

        var idx = EnsureMapIndex();
        if (!idx.TerminalIdToLocation.TryGetValue(terminalId, out var location)) return null;
        if (!idx.CommodityIdToName.TryGetValue(commodityId, out var commodityName)) return null;

        var wantPlayerBuy = IsBuySide(side);
        SctListing? best = null;
        foreach (var r in snap.Rows)
        {
            if (!string.Equals(r.Location, location, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.Equals(r.Commodity, commodityName, StringComparison.OrdinalIgnoreCase)) continue;
            if (SctRowIsPlayerBuy(r.Transaction) != wantPlayerBuy) continue;
            if (best is null || r.TimestampUtc > best.TimestampUtc) best = r;
        }
        return best;
    }

    // The SCT-exclusive corroboration surface: BUYS listings for a commodity at stations that
    // have NO UEX terminal at all (the map's own "SCT-only" entries - both CommodityId and RawId
    // null). Find() above can never reach these (there is no UEX terminal id to key on), so this
    // is the only way that data ever gets in front of a user - e.g. "also buying nearby (SCT-only
    // source)" on the sell-lookup flow. Every kept row's Location is already guaranteed present in
    // the map (RefreshAsync's own join filters on exactly that), so the map lookup below never
    // misses for real cached data.
    /// <summary>
    /// Every SCT listing that maps onto a UEX (terminal, commodity, side), keyed for O(1) lookup.
    ///
    /// Exists because the planner needs a reading for EVERY priced row on every rebuild, and
    /// <see cref="Find"/> scans the whole snapshot per call - roughly ten million string
    /// comparisons across a full rank, on a path the budget box re-runs while the user types.
    /// This walks the snapshot once instead. The key's bool is the PLAYER's buy side, so callers
    /// never have to think about SCT's shop-perspective wording (see SctRowIsPlayerBuy).
    ///
    /// Empty while market data is off, matching every other entry point here.
    /// </summary>
    public IReadOnlyDictionary<(int TerminalId, int CommodityId, bool PlayerBuy), SctListing> SideIndex()
    {
        var empty = new Dictionary<(int, int, bool), SctListing>();
        if (_settings.Current.MarketDataEnabled != true) return empty;
        var snap = _snapshot;
        if (snap is null || snap.Rows.Count == 0) return empty;

        var idx = EnsureMapIndex();
        var locationToTerminals = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (terminalId, location) in idx.TerminalIdToLocation)
        {
            if (!locationToTerminals.TryGetValue(location, out var ids))
                locationToTerminals[location] = ids = new List<int>();
            ids.Add(terminalId);
        }
        var nameToCommodity = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (commodityId, name) in idx.CommodityIdToName) nameToCommodity.TryAdd(name, commodityId);

        var result = new Dictionary<(int, int, bool), SctListing>();
        foreach (var r in snap.Rows)
        {
            if (!locationToTerminals.TryGetValue(r.Location, out var terminalIds)) continue;
            if (!nameToCommodity.TryGetValue(r.Commodity, out var commodityId)) continue;
            var playerBuy = SctRowIsPlayerBuy(r.Transaction);
            // A station can map to more than one UEX terminal (general trade and refinery ore
            // sales share one SCT path), and the newest listing wins - the same tie-break Find
            // applies when several rows match.
            foreach (var terminalId in terminalIds)
            {
                var key = (terminalId, commodityId, playerBuy);
                if (!result.TryGetValue(key, out var best) || r.TimestampUtc > best.TimestampUtc)
                    result[key] = r;
            }
        }
        return result;
    }

    public IReadOnlyList<SctListing> SctOnlyBuyers(int commodityId)
    {
        if (_settings.Current.MarketDataEnabled != true) return Array.Empty<SctListing>();
        var snap = _snapshot;
        if (snap is null || snap.Rows.Count == 0) return Array.Empty<SctListing>();

        var idx = EnsureMapIndex();
        if (!idx.CommodityIdToName.TryGetValue(commodityId, out var commodityName)) return Array.Empty<SctListing>();

        var result = new List<SctListing>();
        foreach (var r in snap.Rows)
        {
            if (!IsBuySide(r.Transaction)) continue;
            if (!string.Equals(r.Commodity, commodityName, StringComparison.OrdinalIgnoreCase)) continue;
            if (!idx.Map.Terminals.TryGetValue(r.Location, out var terminal)) continue;
            if (terminal.CommodityId.HasValue || terminal.RawId.HasValue) continue;   // has a UEX terminal: not SCT-only
            result.Add(r);
        }
        return result;
    }

    // "BUY"/"BUYS" (any case) is a buy side; anything else (including "SELL"/"SELLS") is a sell
    // side. Used both for a caller's side argument and for the raw feed's own Transaction string,
    // so the two compare on the same rule.
    /// <summary>Reads a UEX-side word ("buy"/"sell"), which is written from the PLAYER's point of
    /// view: "buy" is the price the player pays.</summary>
    private static bool IsBuySide(string side) => side.StartsWith("BUY", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Does this SCT row describe the PLAYER's buy side? SCT labels its rows from the SHOP's point
    /// of view, which is the opposite of UEX's: a row tagged SELLS is the shop selling to you (your
    /// BUY), and BUYS is the shop buying from you (your SELL).
    /// <para>
    /// Until 2026-08-03 Find compared the two words directly, pairing every UEX row with SCT's
    /// opposite side and matching nothing at all. Measured against the shipped snapshots: UEX buy
    /// vs SCT BUYS produced 0 matches, while the correct pairings produced 233 and 716 with a
    /// median price delta of 0.0%. So no price was ever corroborated and no disagreement was ever
    /// raised - the feed was fetched, parsed, stored and then cross-checked into nothing.
    /// </para>
    /// <para>
    /// Note this is deliberately NOT the helper SctOnlyBuyers uses. That one wants SCT's own word
    /// read literally (a "buyer" is a shop that buys from you), and it was always correct. One
    /// helper serving both meanings is what let the inversion hide.
    /// </para>
    /// </summary>
    private static bool SctRowIsPlayerBuy(string transaction) =>
        transaction.StartsWith("SELL", StringComparison.OrdinalIgnoreCase);

    // A subscriber must never be able to fault the fetch cycle or Start (fail-closed), same
    // MarketDataService.RaiseChanged contract. Only ever called from Start/RefreshAsync, both of
    // which already returned above when the flag is off - so this never fires while it is off.
    private void RaiseChanged()
    {
        // Type only, no ex argument: a subscriber's exception Message can carry a full
        // %AppData% path (the Windows username) into nexus.log - the same rule every
        // path-adjacent log line in this file follows.
        try { Changed?.Invoke(); }
        catch (Exception ex) { Logger.Error($"{Tag} a sct data subscriber threw ({ex.GetType().Name})"); }
    }

    // Built once (see _mapIndex above) from the embedded SctUexMap: reverse lookups from a UEX id
    // to the SCT free-text name the raw feed carries, which is what Find/SctOnlyBuyers need to
    // filter kept rows by. A UEX terminal id can appear as EITHER a station's general-trade
    // terminal (CommodityId) or its refinery-ore-sales terminal (RawId) - SctUexMap's own role
    // split - so both are indexed into the same dictionary; the embedded map has no id duplicated
    // across two SCT paths.
    private SctMapIndex EnsureMapIndex()
    {
        var existing = _mapIndex;
        if (existing is not null) return existing;

        var map = SctUexMap.LoadEmbedded();
        var terminalIdToLocation = new Dictionary<int, string>();
        foreach (var (path, mapping) in map.Terminals)
        {
            if (mapping.CommodityId.HasValue) terminalIdToLocation.TryAdd(mapping.CommodityId.Value, path);
            if (mapping.RawId.HasValue) terminalIdToLocation.TryAdd(mapping.RawId.Value, path);
        }
        var commodityIdToName = new Dictionary<int, string>();
        foreach (var (name, mapping) in map.Commodities)
        {
            if (mapping.UexId.HasValue) commodityIdToName.TryAdd(mapping.UexId.Value, name);
        }

        var built = new SctMapIndex(map, terminalIdToLocation, commodityIdToName);
        _mapIndex = built;
        return built;
    }

    // How long Dispose waits for a cancelled refresh to finish unwinding (same bound and
    // rationale as MarketDataService.DisposeDrainTimeout: long enough for an aborted request to
    // throw and the snapshot save to complete, short enough that shutdown never visibly stalls).
    private static readonly TimeSpan DisposeDrainTimeout = TimeSpan.FromSeconds(3);

    public void Dispose()
    {
        if (_disposed) return;   // double-Dispose is a no-op (MarketDataService.Dispose precedent)
        _disposed = true;
        // Stop must run on the timer's own dispatcher thread; on shutdown from anywhere else it
        // throws, and a failed stop on a service that is going away is not worth an error
        // (MarketDataService.Dispose's own rationale).

        var pending = _cycleDone?.Task;   // captured BEFORE the cancel, which may clear the field
        // Cancels the in-flight refresh through its linked source. _life is deliberately NOT
        // disposed: that refresh still holds a token derived from it (MarketDataService's own
        // Dispose rationale - see its comment for the full reasoning).
        try { _life.Cancel(); } catch { /* best effort */ }

        // Then WAIT for it. Cancelling alone is not enough: a cancelled cycle publishes no snapshot
        // at all now, but it is still unwinding on its way out - still logging, still able to raise
        // Changed into a subscriber - and none of that may race whatever runs after App.OnExit
        // calls this. Bounded, because a subscriber that blocks inside Changed must not be able to
        // hang shutdown.
        try
        {
            if (pending is not null && !pending.Wait(DisposeDrainTimeout))
                Logger.Error($"{Tag} sct refresh did not stop within {DisposeDrainTimeout.TotalSeconds:0}s; leaving it to finish");
        }
        catch (Exception ex) { Logger.Error($"{Tag} sct refresh did not stop cleanly: {ex.Message}"); }
    }

    // The reverse-lookup pair Find/SctOnlyBuyers need, computed once from the embedded map and
    // cached for the life of the service (see _mapIndex).
    private sealed record SctMapIndex(SctUexMap Map, IReadOnlyDictionary<int, string> TerminalIdToLocation,
        IReadOnlyDictionary<int, string> CommodityIdToName);
}

// In-memory + on-disk cache of the last successfully fetched, joined, freshness-filtered SCT
// listing set. Same atomic tmp+move idiom as MarketSnapshotFile.
internal sealed class SctSnapshot
{
    public int Schema { get; set; } = 1;
    public DateTime FetchedUtc { get; set; }
    public List<SctListing> Rows { get; set; } = new();
}

internal static class SctSnapshotFile
{
    public const long MaxLoadBytes = 16 * 1024 * 1024;
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    public static bool Save(string path, SctSnapshot snapshot)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = path + ".tmp";
            var json = JsonSerializer.Serialize(snapshot, SerializerOptions);
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            // NEITHER the message NOR the exception itself may reach the log: path is always under
            // %AppData%, so it would carry the Windows username into nexus.log (task-16 audit) -
            // and .NET file-IO exception Messages embed that full path, which Logger appends
            // verbatim (ex.ToString()) whenever an exception object is passed. So: operation plus
            // exception TYPE only, and no ex argument.
            Logger.Error($"Failed to save SCT snapshot ({ex.GetType().Name})");
            return false;
        }
    }

    public static SctSnapshot? Load(string path, out string? reason)
    {
        // reason is logged verbatim by SctMarketService.Start (Logger.Info, [NET]-tagged), and path
        // is always under %AppData% - so none of these ever interpolate path into the message, and
        // none interpolates an exception's own Message either (a .NET file-IO Message embeds the
        // full path). Both would carry the Windows username into nexus.log (task-16 audit), so the
        // catch-all below reports the exception TYPE only.
        reason = null;
        if (!File.Exists(path)) { reason = "SCT snapshot file not found"; return null; }
        try
        {
            var info = new FileInfo(path);
            if (info.Length > MaxLoadBytes)
            {
                reason = $"SCT snapshot file exceeds max size ({info.Length} > {MaxLoadBytes} bytes)";
                return null;
            }
            var json = File.ReadAllText(path);
            var snapshot = JsonSerializer.Deserialize<SctSnapshot>(json, SerializerOptions);
            if (snapshot is null) { reason = "Failed to deserialize SCT snapshot"; return null; }
            if (snapshot.Schema != 1)
            {
                reason = $"Unsupported SCT snapshot schema (expected 1, got {snapshot.Schema})";
                return null;
            }
            return snapshot;
        }
        catch (Exception ex)
        {
            reason = $"Error loading SCT snapshot: {ex.GetType().Name}";
            return null;
        }
    }
}
