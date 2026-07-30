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

// The dark SCT (SC Trade Tools) crowdsource-listings cache. FULLY INERT while
// AppSettings.SctDataEnabled is false: every public entry point (Start, RefreshAsync,
// SnapshotFetchedUtc, Find, SctOnlyBuyers) checks the flag FIRST and returns null/empty/no-ops
// before touching the network, the embedded map, or disk - live-checked on every call, not just
// "never populated while off," so flipping the flag off after data was already cached hides it
// again immediately. Owner-only Admin-tab surface (Task 9) until the SCT maintainer conversation
// about free-endpoint app use lands - see docs/superpowers/specs/2026-07-29-trade-api-recon-uex.md
// open items.
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

    private readonly SettingsService _settings;
    private readonly ISctTransport _transport;
    private readonly string _snapshotPath;
    private readonly CancellationTokenSource _life = new();

    private int _busy;
    private volatile SctSnapshot? _snapshot;

    // Cached once, lazily, and only from a code path already gated by the flag check (never
    // touched while SctDataEnabled is false - "zero map load while off" applies here too, not
    // just to RefreshAsync's own load). A benign race (two callers both building it at once) is
    // acceptable: the embedded resource never changes at runtime, so both builds are identical.
    private volatile SctMapIndex? _mapIndex;

    internal SctSnapshot? Snapshot => _snapshot;
    public bool FetchInProgress => Volatile.Read(ref _busy) != 0;

    // Null while the flag is off, OR while it is on but no cycle (this run or a previous one, via
    // Start()) has ever produced a snapshot yet. Read live on every access, matching Find/
    // SctOnlyBuyers below - not cached at construction time.
    public DateTime? SnapshotFetchedUtc =>
        _settings.Current.SctDataEnabled == true ? _snapshot?.FetchedUtc : null;

    // Raised on a worker thread (RefreshAsync) or the caller's own thread (Start's disk load)
    // after a new snapshot is published; UI subscribers marshal with Dispatcher.Invoke themselves
    // (the MarketDataService.Changed contract). Both call sites that raise it are themselves only
    // reachable when the flag is on, so this never fires while SctDataEnabled is false.
    public event Action? Changed;

    public SctMarketService(SettingsService settings)
        : this(settings, new HttpSctTransport(), Path.Combine(AppPaths.Root, "cache", "sct_snapshot.json"))
    { }

    internal SctMarketService(SettingsService settings, ISctTransport transport, string snapshotPath)
    {
        _settings = settings;
        _transport = transport;
        _snapshotPath = snapshotPath;
    }

    // Called once from app startup. Loads any cache from a PREVIOUS on-period - fully skipped
    // while the flag is off, so a user who tried this once and turned it back off carries no
    // residual disk read on every later launch.
    public void Start()
    {
        if (_settings.Current.SctDataEnabled != true)
        {
            Logger.Info($"{Tag} sct: dark flag off, not starting");
            return;
        }
        var loaded = SctSnapshotFile.Load(_snapshotPath, out var reason);
        if (loaded is null)
        {
            Logger.Info($"{Tag} sct snapshot not loaded: {reason ?? "no snapshot"}");
            return;
        }
        _snapshot = loaded;
        Logger.Info($"{Tag} sct snapshot loaded: {loaded.Rows.Count} listing(s), fetched {loaded.FetchedUtc:yyyy-MM-dd HH:mm} UTC");
        RaiseChanged();
    }

    // The one fetch cycle: page the crowdsource-listings endpoint, cut anything older than
    // MaxListingAge, drop anything the map cannot resolve to a known SCT shop path, cache the
    // survivors. nowUtc defaults to DateTime.UtcNow in production; tests pin it explicitly so the
    // freshness cutoff is deterministic rather than a function of whatever day this suite happens
    // to run (the same reasoning as Logger.WriteTo's testable nowUtc parameter).
    public async Task RefreshAsync(bool manual, DateTime? nowUtc = null)
    {
        if (_settings.Current.SctDataEnabled != true) return;   // every public entry point checks first
        if (Interlocked.Exchange(ref _busy, 1) != 0) return;    // single-flight

        try
        {
            Logger.Info($"{Tag} sct refresh started ({(manual ? "manual" : "auto")})");
            var utcNow = nowUtc ?? DateTime.UtcNow;

            // Map load happens ONLY here, after the flag check above - "zero map load while off".
            var map = EnsureMapIndex().Map;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(_life.Token);
            cts.CancelAfter(FetchDeadline);

            var all = new List<SctListing>();
            int totalSkippedRows = 0, page = 0;
            while (page < MaxPages)
            {
                string body;
                try
                {
                    body = await _transport.GetStringAsync($"{BaseUrl}{ListingsEndpoint}?page={page}",
                        MaxResponseBytes, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    Logger.Error($"{Tag} sct refresh stopped: deadline ({FetchDeadline.TotalMinutes:0}m) reached at page {page}");
                    break;
                }
                catch (Exception ex)
                {
                    Logger.Error($"{Tag} sct fetch page {page} failed: {ex.Message}");
                    break;
                }

                var rows = SctListingParser.Parse(body, out var skipped);
                totalSkippedRows += skipped;
                if (rows.Count == 0) break;   // last page (or an empty/malformed one): stop paging

                all.AddRange(rows);
                page++;
                if (page < MaxPages)
                    await Task.Delay(PageSpacing, cts.Token).ConfigureAwait(false);   // polite spacing
            }

            var fresh = SctListingParser.Fresh(all, MaxListingAge, utcNow);

            var kept = new List<SctListing>();
            int droppedUnmapped = 0;
            foreach (var r in fresh)
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
                        $"{kept.Count} kept, {droppedUnmapped} unmapped");
            RaiseChanged();
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
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
        if (_settings.Current.SctDataEnabled != true) return null;
        var snap = _snapshot;
        if (snap is null || snap.Rows.Count == 0) return null;

        var idx = EnsureMapIndex();
        if (!idx.TerminalIdToLocation.TryGetValue(terminalId, out var location)) return null;
        if (!idx.CommodityIdToName.TryGetValue(commodityId, out var commodityName)) return null;

        var wantBuy = IsBuySide(side);
        SctListing? best = null;
        foreach (var r in snap.Rows)
        {
            if (!string.Equals(r.Location, location, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.Equals(r.Commodity, commodityName, StringComparison.OrdinalIgnoreCase)) continue;
            if (IsBuySide(r.Transaction) != wantBuy) continue;
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
    public IReadOnlyList<SctListing> SctOnlyBuyers(int commodityId)
    {
        if (_settings.Current.SctDataEnabled != true) return Array.Empty<SctListing>();
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
    private static bool IsBuySide(string side) => side.StartsWith("BUY", StringComparison.OrdinalIgnoreCase);

    // A subscriber must never be able to fault the fetch cycle or Start (fail-closed), same
    // MarketDataService.RaiseChanged contract. Only ever called from Start/RefreshAsync, both of
    // which already returned above when the flag is off - so this never fires while it is off.
    private void RaiseChanged()
    {
        try { Changed?.Invoke(); }
        catch (Exception ex) { Logger.Error($"{Tag} a sct data subscriber threw", ex); }
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

    public void Dispose()
    {
        try { _life.Cancel(); } catch { /* best effort */ }
        _life.Dispose();
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
            Logger.Error($"Failed to save SCT snapshot to {path}", ex);
            return false;
        }
    }

    public static SctSnapshot? Load(string path, out string? reason)
    {
        reason = null;
        if (!File.Exists(path)) { reason = $"SCT snapshot file not found: {path}"; return null; }
        try
        {
            var info = new FileInfo(path);
            if (info.Length > MaxLoadBytes)
            {
                reason = $"SCT snapshot file exceeds max size ({info.Length} > {MaxLoadBytes} bytes): {path}";
                return null;
            }
            var json = File.ReadAllText(path);
            var snapshot = JsonSerializer.Deserialize<SctSnapshot>(json, SerializerOptions);
            if (snapshot is null) { reason = $"Failed to deserialize SCT snapshot: {path}"; return null; }
            if (snapshot.Schema != 1)
            {
                reason = $"Unsupported SCT snapshot schema (expected 1, got {snapshot.Schema}): {path}";
                return null;
            }
            return snapshot;
        }
        catch (Exception ex)
        {
            reason = $"Error loading SCT snapshot: {path} - {ex.Message}";
            return null;
        }
    }
}
