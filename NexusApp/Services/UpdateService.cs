using System.IO;
using System.Net.Http;
using System.Text;

namespace NexusApp.Services;

// Names are load-bearing: UpdateNotice.StatusLine matches them as strings.
public enum UpdateState { Idle, Checking, UpToDate, UpdateAvailable, Downloading, Verifying, ReadyToInstall, Installing, ManualHandoff, Failed }

// Why a check failed, in terms a user can act on. Classified from the HTTP status and which
// URL failed, never from a response body (nothing unauthenticated is read). None covers every
// other failure and keeps the generic "see nexus.log" line. Names are load-bearing the same
// way UpdateState's are: UpdateNotice.StatusLine matches them as strings.
public enum UpdateFailureKind { None, NotSignedYet, SignatureMissing, Network }

// Seam between the state machine and the network, so every state transition is testable
// with a fake and the real HTTP code stays in one small class.
internal interface IUpdateTransport
{
    Task<byte[]> GetBytesAsync(string url, int maxBytes, CancellationToken ct);
    Task DownloadFileAsync(string url, string destPath, long expectedSize, IProgress<long>? progress, CancellationToken ct);
}

internal sealed class HttpUpdateTransport : IUpdateTransport
{
    // 15s covers a slow manifest fetch; the download path replaces it with its own 10-minute
    // linked token below (HttpClient.Timeout does not govern streamed body reads).
    private static readonly HttpClient _http = Create();

    private static HttpClient Create()
    {
        var handler = new SocketsHttpHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 5 };
        var c = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        // GitHub requires a User-Agent. Carries the app version only, nothing about the user.
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"NexusApp-Update/{NexusApp.AppInfo.Version}");
        return c;
    }

    public async Task<byte[]> GetBytesAsync(string url, int maxBytes, CancellationToken ct)
    {
        // Explicit time bound: HttpClient.Timeout does not govern streamed body reads under
        // ResponseHeadersRead, and a dribbling endpoint must not wedge the single-flight
        // flag for the rest of the process. 30s is generous for a 16 KB manifest.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
        EnsureHttps(resp);
        resp.EnsureSuccessStatusCode();
        if (resp.Content.Headers.ContentLength is { } len && len > maxBytes)
            throw new InvalidOperationException("response larger than expected");
        await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
        using var ms = new MemoryStream();
        await CopyCappedAsync(stream, ms, maxBytes, null, cts.Token).ConfigureAwait(false);
        return ms.ToArray();
    }

    public async Task DownloadFileAsync(string url, string destPath, long expectedSize, IProgress<long>? progress, CancellationToken ct)
    {
        // A big asset on a slow line can legitimately take minutes; 10 is the giving-up point.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMinutes(10));
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
        EnsureHttps(resp);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
        long total;
        await using (var file = File.Create(destPath))
            total = await CopyCappedAsync(stream, file, expectedSize, progress, cts.Token).ConfigureAwait(false);
        if (total != expectedSize)
            throw new InvalidOperationException("download size did not match the manifest");
    }

    // Defense in depth: even if the handler ever followed a downgrade redirect, the final
    // response must have arrived over https or it is discarded. The guarantee is ours,
    // not the framework's.
    private static void EnsureHttps(HttpResponseMessage resp)
    {
        if (resp.RequestMessage?.RequestUri?.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("response did not arrive over https");
    }

    // Copies at most maxBytes; one byte more aborts. The cap comes from the SIGNED manifest
    // (or the manifest's own fixed cap), so a lying server cannot flood the disk.
    private static async Task<long> CopyCappedAsync(Stream from, Stream to, long maxBytes, IProgress<long>? progress, CancellationToken ct)
    {
        var buf = new byte[81920];
        long total = 0;
        int n;
        while ((n = await from.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
        {
            total += n;
            if (total > maxBytes) throw new InvalidOperationException("response larger than expected");
            await to.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
            progress?.Report(total);
        }
        return total;
    }
}

// The update subsystem is the only code in the app that touches the network, and this class
// (with its transport above) is where that happens. Owns the update state machine,
// the on-launch auto-check gate, single-flight, and the temp download area.
public sealed class UpdateService
{
    public const string Tag = "[UPDATE]";
    public const string ManifestUrl = "https://github.com/T3SoD/NexusApp/releases/latest/download/update_manifest.json";
    public const string SignatureUrl = "https://github.com/T3SoD/NexusApp/releases/latest/download/update_manifest.json.sig";

    // A P-256 signature is 64 bytes, 88 as base64; 4 KB tolerates whitespace without
    // letting a hostile response waste memory.
    internal const int MaxSignatureBytes = 4 * 1024;

    private readonly SettingsService _settings;
    private readonly IUpdateTransport _transport;
    private readonly Func<byte[], byte[], bool> _verify;
    private readonly Func<string> _distribution;
    private readonly string _currentVersion;
    private readonly string _updatesDir;
    private readonly bool _demo;
    private readonly Func<string, bool> _startProcess;
    private readonly IPortableSwapper _swapper;
    private readonly Func<bool> _purgeGuard;
    private readonly Func<string?> _relaunchPath;
    private int _busy;   // Interlocked single-flight across check and download

    public UpdateState State { get; private set; } = UpdateState.Idle;
    public UpdateManifest? Available { get; private set; }
    public string? DownloadedPath { get; private set; }
    public string? LastFailure { get; private set; }
    public bool LastFailureWasUserInitiated { get; private set; }

    // Classification of the most recent failure, so the Settings row can name the cause instead
    // of sending every failure to nexus.log. Every Fail sets it; None means unclassified.
    public UpdateFailureKind LastFailureKind { get; private set; } = UpdateFailureKind.None;

    // True when the most recent failure was the hash-verification refusal (as opposed to a
    // network or parse problem): the UI shows its "verification failed" warning only for this.
    public bool LastFailureWasVerification { get; private set; }
    public long DownloadedBytes { get; private set; }
    public long TotalBytes { get; private set; }

    // Portable self-swap surface. Availability is evaluated once per download (and honors
    // PreferManualUpdate); the failure note renders PrepareFailedBody without leaving
    // ReadyToInstall, because StatusLine's Failed arm narrates CHECK failures and would lie.
    public bool PortableSwapAvailable { get; private set; }
    public string? PortableSwapUnavailableReason { get; private set; }
    public string? LastPortableApplyFailure { get; private set; }

    // True when the last apply ended with the rollback stuck: the previous version is sitting
    // in .old files that only startup recovery can put back, so the surfaces show the
    // restart-to-restore copy instead of an unusable Try again.
    public bool LastApplyLeftRestorePending { get; private set; }
    public bool PortableApplyInProgress { get; private set; }
    public bool ManualUnpackInProgress { get; private set; }

    // Set only after a Completed swap; App.OnExit spawns this path as the process's LAST
    // action so the new instance never overlaps this one's settings and database writes.
    public string? PendingRelaunchPath { get; private set; }

    // Session-scoped: the swap-failed strip's "Update manually" choice routes the next
    // ReadyToInstall straight to the guided manual flow.
    public bool PreferManualUpdate { get; set; }

    // Raised on the worker thread after every state change; UI subscribers marshal with
    // Dispatcher.Invoke themselves (the App.Shards.Changed pattern).
    public event Action? Changed;

    public UpdateService(SettingsService settings)
        : this(settings, new HttpUpdateTransport(), UpdateVerifier.VerifySignature,
               () => NexusApp.AppInfo.Distribution, NexusApp.AppInfo.Version,
               Path.Combine(AppPaths.Root, "updates"), AppPaths.IsDemoProfile)
    { }

    internal UpdateService(SettingsService settings, IUpdateTransport transport, Func<byte[], byte[], bool> verify,
                           Func<string> distribution, string currentVersion, string updatesDir, bool isDemoProfile,
                           Func<string, bool>? startProcess = null, IPortableSwapper? swapper = null,
                           Func<bool>? purgeGuard = null, Func<string?>? relaunchPathProvider = null)
    {
        _settings = settings;
        _transport = transport;
        _verify = verify;
        _distribution = distribution;
        _currentVersion = currentVersion;
        _updatesDir = updatesDir;
        _demo = isDemoProfile;
        // A null Process from Start means nothing was launched (the shell declined to reuse or
        // create one), so report failure rather than shutting the app down for an installer
        // that is not running.
        _startProcess = startProcess ??
            (path => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }) is not null);
        _swapper = swapper ?? new PortableUpdater();
        // The journal existence check guards the purge: while one exists, recovery owns
        // everything under updates\ (the verified zip is the crash-recovery artifact).
        _purgeGuard = purgeGuard ?? (() => File.Exists(SwapJournal.DefaultPath));
        _relaunchPath = relaunchPathProvider ?? (() => Environment.ProcessPath);
    }

    // Pure gate for the on-launch check: consent must be an explicit yes, and the demo profile
    // is always inert. The 24-hour throttle that used to live here died 2026-08-01, when the
    // check changed to run on each launch of the app: launch is the only auto trigger, so a
    // same-day relaunch now checks again instead of silently sitting on a yesterday-stamped
    // result - one signed-manifest HEAD-sized fetch per launch is nothing, and it took the
    // clock-rollback self-heal with it (no stamp comparison, nothing to heal).
    // LastUpdateCheckUtc is still stamped after every completed attempt: Settings shows it.
    internal static bool ShouldAutoCheck(bool? enabled, bool isDemoProfile)
        => !isDemoProfile && enabled == true;

    // Assets come from the VERSIONED release path built from the verified manifest version
    // (never releases/latest, which could race a newer release mid-flow) and a whitelisted
    // asset name. A parsed Version cannot carry path or URL metacharacters.
    internal static string AssetUrl(Version version, string assetName) =>
        $"https://github.com/T3SoD/NexusApp/releases/download/v{version.ToString(3)}/{assetName}";

    public async Task CheckAsync(bool manual)
    {
        if (_demo) return;
        if (Interlocked.Exchange(ref _busy, 1) != 0) return;
        var completed = false;
        try
        {
            LastFailure = null;
            LastFailureKind = UpdateFailureKind.None;
            LastFailureWasUserInitiated = manual;
            SetState(UpdateState.Checking);
            Logger.Info($"{Tag} check started ({(manual ? "manual" : "auto")})");

            // Fetched in two blocks purely so a failure knows WHICH url it was: same order, same
            // fail-closed handling, same LastUpdateCheckUtc stamp as one combined try.
            byte[] manifestBytes, sigText;
            try
            {
                manifestBytes = await _transport.GetBytesAsync(ManifestUrl, UpdateManifest.MaxManifestBytes, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) { completed = true; FailFetch(ex, isManifest: true); return; }
            try
            {
                sigText = await _transport.GetBytesAsync(SignatureUrl, MaxSignatureBytes, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) { completed = true; FailFetch(ex, isManifest: false); return; }
            completed = true;   // a completed network attempt stamps LastUpdateCheckUtc either way

            byte[] sig;
            try { sig = Convert.FromBase64String(Encoding.UTF8.GetString(sigText).Trim()); }
            catch (FormatException) { Fail("check failed: the signature file is not valid base64"); return; }

            // Signature FIRST, over the exact raw bytes. Nothing unauthenticated is parsed.
            if (!_verify(manifestBytes, sig)) { Fail("check failed: the manifest signature did not verify"); return; }

            UpdateManifest manifest;
            try { manifest = UpdateManifest.Parse(manifestBytes); }
            catch (UpdateManifestException ex)
            {
                // A schema newer than this build is not a failure: those bytes were already
                // signature-verified, so the publisher genuinely shipped a manifest for a later
                // Nexus. There is nothing this build can install, which reads as up to date.
                if (ex.SchemaTooNew)
                {
                    Available = null;
                    Logger.Info($"{Tag} check result: this update needs a newer Nexus");
                    SetState(UpdateState.UpToDate);
                    return;
                }
                Fail($"check failed: {Reason(ex)}");
                return;
            }

            if (!UpdateVerifier.IsUpgrade(_currentVersion, manifest.Version.ToString(3)))
            {
                Available = null;
                Logger.Info($"{Tag} check result: up to date ({_currentVersion})");
                SetState(UpdateState.UpToDate);
            }
            else
            {
                Available = manifest;
                Logger.Info($"{Tag} check result: {manifest.Version.ToString(3)} available");
                SetState(UpdateState.UpdateAvailable);
            }
        }
        finally
        {
            if (completed)
            {
                _settings.Current.LastUpdateCheckUtc = DateTime.UtcNow;
                _settings.Save();
            }
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    public async Task DownloadAsync()
    {
        if (State != UpdateState.UpdateAvailable || Available is null) return;
        var asset = Available.AssetFor(_distribution());
        if (asset is null) { Fail("download unavailable: no asset for this distribution"); return; }
        if (Interlocked.Exchange(ref _busy, 1) != 0) return;
        try
        {
            LastFailureWasUserInitiated = true;   // downloads are always a click
            LastFailureWasVerification = false;
            // A retry must not leave the previous attempt's failure text or a stale path behind:
            // the UI reads both while this run is in flight.
            LastFailure = null;
            DownloadedPath = null;
            var version = Available.Version.ToString(3);
            var dir = Path.Combine(_updatesDir, version);
            try { Directory.CreateDirectory(dir); }
            catch (Exception ex) { Fail($"couldn't prepare the download folder: {Reason(ex)}", ex); return; }
            var dest = Path.Combine(dir, asset.Name);
            var partial = dest + ".partial";
            TotalBytes = asset.Size;
            DownloadedBytes = 0;
            SetState(UpdateState.Downloading);
            Logger.Info($"{Tag} download started: {asset.Name} {asset.Size} bytes");
            try
            {
                // Throttled: one raise per 80 KB chunk would trigger ~1250 full UI refreshes
                // over a typical download. Whole-MB steps plus the final byte are plenty.
                long lastRaised = 0;
                // Synchronous by design: subscribers marshal to the UI thread themselves (the
                // Changed contract), and posting through the captured SynchronizationContext
                // both reordered reports and flooded the dispatcher.
                var progress = new SyncProgress(b =>
                {
                    DownloadedBytes = b;
                    if (b - lastRaised >= 1_048_576 || b >= TotalBytes) { lastRaised = b; RaiseChanged(); }
                });
                await _transport.DownloadFileAsync(AssetUrl(Available.Version, asset.Name), partial, asset.Size, progress, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) { TryDelete(partial); Fail($"download failed: {Reason(ex)}", ex); return; }

            SetState(UpdateState.Verifying);
            bool ok;
            try { ok = UpdateVerifier.FileHashMatches(partial, asset.Sha256); }
            catch (Exception ex) { TryDelete(partial); Fail($"verification error: {Reason(ex)}", ex); return; }
            if (!ok)
            {
                TryDelete(partial);
                LastFailureWasVerification = true;
                Fail("verification FAILED, file deleted");
                return;
            }
            // The rename is a failure point too (a realtime AV scanner can hold the fresh
            // .exe): it must land in Failed with the partial cleaned up, like every other path.
            try { File.Move(partial, dest, overwrite: true); }
            catch (Exception ex) { TryDelete(partial); Fail($"couldn't finalize the download: {Reason(ex)}", ex); return; }
            DownloadedPath = dest;
            Logger.Info($"{Tag} download complete, hash verified");
            if (_distribution() == "Portable") EvaluatePortablePreflight(asset.Size);
            SetState(UpdateState.ReadyToInstall);
        }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    // Re-verifies the hash immediately before launch (narrows the verify-to-execute window),
    // starts the installer, and reports success. The CALLER shuts the app down afterwards,
    // mirroring the DemoProfile.StartDemoInstance ordering (child confirmed before parent exits).
    public bool LaunchInstaller()
    {
        if (State != UpdateState.ReadyToInstall || DownloadedPath is null || Available is null) return false;
        // A distribution mismatch is a caller bug, not a tamper signal: comparing the
        // portable zip against the installer hash would "fail verification" and delete the
        // user's valid download. Refuse without touching anything.
        if (_distribution() != "Installer")
        {
            Logger.Info($"{Tag} install refused: portable distribution");
            return false;
        }
        var asset = Available.AssetFor("Installer");
        try
        {
            if (asset is null || !UpdateVerifier.FileHashMatches(DownloadedPath, asset.Sha256))
            {
                TryDelete(DownloadedPath);
                DownloadedPath = null;
                LastFailureWasVerification = true;
                Fail("verification FAILED at install time, file deleted");
                return false;
            }
            Logger.Info($"{Tag} launching installer {Available.Version.ToString(3)}");
            var ok = _startProcess(DownloadedPath);
            if (!ok) { Fail("couldn't start the installer"); return false; }
            SetState(UpdateState.Installing);
            return true;
        }
        catch (Exception ex)
        {
            // The file vanished between download and launch (a cleaner, AV quarantine, a manual
            // delete): there is nothing left to retry, so drop the path. Every other exception
            // leaves it in place so the user can press install again.
            if (ex is FileNotFoundException or DirectoryNotFoundException) DownloadedPath = null;
            Fail($"couldn't start the installer: {Reason(ex)}", ex);
            return false;
        }
    }

    // Decides which portable strip the UI shows the moment ReadyToInstall lands: the
    // self-swap confirm, or the guided manual flow (with the reason logged, never shown as
    // an error). Cheap probes only; Apply re-runs the full gate at click time.
    private void EvaluatePortablePreflight(long zipBytes)
    {
        LastPortableApplyFailure = null;
        if (PreferManualUpdate)
        {
            PortableSwapAvailable = false;
            PortableSwapUnavailableReason = "manual update chosen";
            Logger.Info($"{Tag} portable swap unavailable: manual update chosen, offering manual handoff");
            return;
        }
        // A pending journal means the engine will refuse this swap anyway (its .old files can
        // be the only copy of a previous version), so the strip routes straight to the guided
        // manual flow instead of offering a Try again that cannot work until a restart. The
        // manual copy converges safely: startup recovery heals the journal to Complete once
        // the running version equals the attempted one.
        if (_purgeGuard())
        {
            PortableSwapAvailable = false;
            PortableSwapUnavailableReason = "a previous update has not finished; restart Nexus to complete it";
            Logger.Info($"{Tag} portable swap unavailable: {PortableSwapUnavailableReason}, offering manual handoff");
            return;
        }
        PortablePreflight pre;
        try { pre = _swapper.Preflight(zipBytes); }
        catch (Exception ex)
        {
            // The probes are individually defensive, but this call must never be able to
            // strand a verified download short of ReadyToInstall: an escaped exception is a
            // bug, and the honest landing is the manual flow with the cause in nexus.log.
            pre = new PortablePreflight(false, "the update check could not inspect this install");
            Logger.Error($"{Tag} portable preflight threw unexpectedly", ex);
        }
        PortableSwapAvailable = pre.Ok;
        PortableSwapUnavailableReason = pre.Ok ? null : pre.Reason;
        if (pre.Ok) Logger.Info($"{Tag} portable swap preconditions ok");
        else Logger.Info($"{Tag} portable swap unavailable: {pre.Reason}, offering manual handoff");
    }

    // The portable twin of LaunchInstaller: same click-gated re-verify philosophy, but the
    // whole verify-extract-stage-flip sequence runs here (via PortableUpdater) while the app
    // stays alive to roll back. True = files flipped; the CALLER shuts the app down and
    // App.OnExit spawns PendingRelaunchPath last.
    public async Task<bool> ApplyPortableAsync()
    {
        if (_demo) return false;
        if (State != UpdateState.ReadyToInstall || DownloadedPath is null || Available is null) return false;
        if (_distribution() != "Portable")
        {
            Logger.Info($"{Tag} portable swap refused: not the portable distribution");
            return false;
        }
        var asset = Available.AssetFor("Portable");
        if (asset is null) return false;
        if (Interlocked.Exchange(ref _busy, 1) != 0) return false;
        try
        {
            LastPortableApplyFailure = null;
            LastApplyLeftRestorePending = false;
            LastFailureWasUserInitiated = true;
            var version = Available.Version;
            var downloaded = DownloadedPath;
            PortableApplyInProgress = true;
            SetState(UpdateState.Installing);
            PortableApplyResult result;
            try
            {
                result = await Task.Run(() => _swapper.Apply(downloaded, asset.Sha256, version, _currentVersion)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // The engine reports failures as results; an exception escaping it is a bug,
                // but the state machine must still land somewhere honest and recoverable.
                result = new PortableApplyResult(PortableApplyOutcome.FailedNothingChanged, Reason(ex));
                Logger.Error($"{Tag} portable swap threw unexpectedly", ex);
            }
            if (result.Outcome == PortableApplyOutcome.Completed)
            {
                PendingRelaunchPath = _relaunchPath();
                Logger.Info($"{Tag} handing off to relaunch, closing");
                return true;
            }
            LastPortableApplyFailure = TextSanitizer.ForLog(result.Reason);
            LastApplyLeftRestorePending = result.Outcome == PortableApplyOutcome.FailedRollbackIncomplete;
            // The engine logs its own rollback story, but the FailedNothingChanged returns
            // are silent by design there; this line guarantees EVERY user-visible apply
            // failure has a nexus.log counterpart (feature-logging rule).
            Logger.Error($"{Tag} portable swap failed: {LastPortableApplyFailure}");
            PortableApplyInProgress = false;
            SetState(UpdateState.ReadyToInstall);
            return false;
        }
        finally
        {
            if (State != UpdateState.Installing) PortableApplyInProgress = false;
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    // The guided manual flow: verify + unpack + open both folders, then hold in
    // ManualHandoff while the user does the copy and closes Nexus themselves.
    public async Task UnpackForManualAsync()
    {
        if (_demo) return;
        if (State != UpdateState.ReadyToInstall || DownloadedPath is null || Available is null) return;
        if (_distribution() != "Portable") return;
        var asset = Available.AssetFor("Portable");
        if (asset is null) return;
        if (Interlocked.Exchange(ref _busy, 1) != 0) return;
        try
        {
            LastPortableApplyFailure = null;
            // A manual unpack is a different attempt: its own failure must regain the Try again
            // affordance instead of inheriting the earlier swap's restart-to-restore copy.
            LastApplyLeftRestorePending = false;
            LastFailureWasUserInitiated = true;
            var version = Available.Version;
            var downloaded = DownloadedPath;
            ManualUnpackInProgress = true;
            SetState(UpdateState.Installing);
            var ok = await Task.Run(() => _swapper.UnpackForManual(downloaded, asset.Sha256, version)).ConfigureAwait(false);
            ManualUnpackInProgress = false;
            if (ok) SetState(UpdateState.ManualHandoff);
            else
            {
                LastPortableApplyFailure = "couldn't unpack the update";
                SetState(UpdateState.ReadyToInstall);
            }
        }
        finally
        {
            ManualUnpackInProgress = false;
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    // Portable handoff: reveal the verified download so the user can do the folder swap.
    public void OpenDownloadFolder()
    {
        if (DownloadedPath is null) return;
        try
        {
            var dir = Path.GetDirectoryName(DownloadedPath)!;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
            Logger.Info($"{Tag} portable update folder opened");
        }
        catch (Exception ex) { Logger.Error($"{Tag} couldn't open the download folder", ex); }
    }

    // Startup hygiene: anything under updates\ is from a previous session (finished installs,
    // aborted .partial files) and gets removed wholesale before any new download starts.
    public void PurgeStaleDownloads()
    {
        // While a swap journal exists, startup recovery owns everything under updates\ (the
        // verified zip is the crash-recovery artifact); purging now would destroy it.
        if (_purgeGuard())
        {
            Logger.Info($"{Tag} purge skipped: a swap journal is present");
            return;
        }
        // Not an error: the usual cause is an installer the user left open holding its own file.
        // Housekeeping that will succeed on a later start does not deserve an ERROR line.
        try { if (Directory.Exists(_updatesDir)) Directory.Delete(_updatesDir, recursive: true); }
        catch { Logger.Info($"{Tag} couldn't purge stale downloads (a previous installer may still be open); will retry next start"); }
    }

    // Reports on the calling (transport) thread instead of posting to a captured
    // SynchronizationContext: progress must be observable in order and without flooding the
    // dispatcher, and subscribers already marshal to the UI thread themselves.
    private sealed class SyncProgress : IProgress<long>
    {
        private readonly Action<long> _report;
        public SyncProgress(Action<long> report) { _report = report; }
        public void Report(long value) => _report(value);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort; purge gets it next start */ }
    }

    private void SetState(UpdateState s)
    {
        State = s;
        RaiseChanged();
    }

    // A subscriber must never be able to fault the update flow (fail-closed). The real
    // case is a UI handler calling Dispatcher.Invoke while the app is shutting down.
    private void RaiseChanged()
    {
        try { Changed?.Invoke(); }
        catch (Exception ex) { Logger.Error($"{Tag} a state-change subscriber threw", ex); }
    }

    // A failed manifest or signature fetch, said in plain words. The only inputs are the HTTP
    // status and which url failed: no response body is read here, because nothing is authentic
    // until the signature check. Both 404 lines are expected states of the release channel (a
    // release published but not signed yet, or signed only halfway), so they carry no stack
    // trace; the Logger has no Warn level, so they stay ERROR like every other Fail.
    private void FailFetch(Exception ex, bool isManifest)
    {
        if ((ex as HttpRequestException)?.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            if (isManifest)
                Fail("check failed: the latest release is not signed yet (no update manifest published); updates stay hidden until it is signed",
                     kind: UpdateFailureKind.NotSignedYet);
            else
                Fail("check failed: the update manifest is present but its signature is missing (incomplete signing)",
                     kind: UpdateFailureKind.SignatureMissing);
            return;
        }
        // Timeout, DNS, TLS, any non-404 status: one story for the user, full detail in the log.
        Fail($"check failed: network error: {Reason(ex)}", ex, UpdateFailureKind.Network);
    }

    private void Fail(string reason, Exception? ex = null, UpdateFailureKind kind = UpdateFailureKind.None)
    {
        LastFailure = TextSanitizer.ForLog(reason);
        LastFailureKind = kind;
        Logger.Error($"{Tag} {LastFailure}", ex);
        SetState(UpdateState.Failed);
    }

    private static string Reason(Exception ex) => TextSanitizer.ForLog(ex.Message);
}
