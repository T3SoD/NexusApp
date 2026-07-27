using System.Net;
using System.Security.Cryptography;
using System.Text;
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// State machine and check flow, tested with a fake transport and an in-test keypair: no
// network, no production key. The properties that must hold: nothing unauthenticated is
// parsed (signature first), the strictly-greater rule decides availability, every failure
// lands in Failed with a sanitized reason, the throttle and single-flight behave, and the
// demo profile is inert.
public class UpdateServiceTests : IDisposable
{
    // Every Make/Make2 mints a temp tree under %TEMP% that real downloads and settings land in.
    // xUnit builds a fresh instance per test, so Dispose sweeps exactly this test's directories
    // instead of leaving hundreds of them behind across a full run.
    private readonly List<string> _tempDirs = new();

    public void Dispose()
    {
        foreach (var d in _tempDirs)
        {
            try { if (Directory.Exists(d)) Directory.Delete(d, recursive: true); }
            catch { /* best effort: a stray temp dir must never fail a green test */ }
        }
    }

    private sealed class FakeTransport : IUpdateTransport
    {
        public Dictionary<string, byte[]> Responses { get; } = new();
        public Dictionary<string, byte[]> Files { get; } = new();
        public List<string> Requested { get; } = new();
        public Exception? ThrowOnGet { get; set; }

        // Opt-in chunked mode: 0 keeps the original single terminal report, so every existing
        // test is unaffected. A positive value mimics the real transport's per-read reporting
        // so the Changed-raise throttle can be observed. ProgressReports counts the callbacks.
        public int ProgressChunkBytes { get; set; }
        public int ProgressReports { get; private set; }

        public Task<byte[]> GetBytesAsync(string url, int maxBytes, CancellationToken ct)
        {
            Requested.Add(url);
            if (ThrowOnGet is not null) throw ThrowOnGet;
            // Missing asset means the real transport's EnsureSuccessStatusCode would throw an
            // HttpRequestException carrying 404, which is exactly what the check classifies on.
            if (!Responses.TryGetValue(url, out var b))
                throw new HttpRequestException("Response status code does not indicate success: 404 (Not Found).", null, HttpStatusCode.NotFound);
            if (b.Length > maxBytes) throw new InvalidOperationException("response larger than expected");
            return Task.FromResult(b);
        }

        public Task DownloadFileAsync(string url, string destPath, long expectedSize, IProgress<long>? progress, CancellationToken ct)
        {
            Requested.Add(url);
            if (!Files.TryGetValue(url, out var b)) throw new InvalidOperationException("404");
            if (b.LongLength != expectedSize) throw new InvalidOperationException("download size did not match the manifest");
            File.WriteAllBytes(destPath, b);
            if (ProgressChunkBytes > 0)
                for (long done = ProgressChunkBytes; done < b.LongLength; done += ProgressChunkBytes)
                {
                    ProgressReports++;
                    progress?.Report(done);
                }
            ProgressReports++;
            progress?.Report(b.LongLength);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeSwapper : IPortableSwapper
    {
        public PortablePreflight PreflightResult { get; set; } = new(true, "");
        public PortableApplyResult ApplyResult { get; set; } = new(PortableApplyOutcome.Completed, "");
        public bool UnpackResult { get; set; } = true;
        public Exception? PreflightThrows { get; set; }
        public int PreflightCalls; public int ApplyCalls; public int UnpackCalls;
        public PortablePreflight Preflight(long zipBytes)
        {
            PreflightCalls++;
            if (PreflightThrows is not null) throw PreflightThrows;
            return PreflightResult;
        }
        public PortableApplyResult Apply(string zipPath, string expectedSha256, Version version, string currentVersion)
        { ApplyCalls++; return ApplyResult; }
        public bool UnpackForManual(string zipPath, string expectedSha256, Version version)
        { UnpackCalls++; return UnpackResult; }
    }

    private static readonly (string priv, string pub) Key = NewKeyPair();

    private static (string, string) NewKeyPair()
    {
        using var e = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (e.ExportPkcs8PrivateKeyPem(), e.ExportSubjectPublicKeyInfoPem());
    }

    private static byte[] Sign(byte[] data)
    {
        using var e = ECDsa.Create();
        e.ImportFromPem(Key.priv);
        return e.SignData(data, HashAlgorithmName.SHA256);
    }

    // Publishes a signed manifest into the fake transport. Returns the manifest bytes.
    private static byte[] Publish(FakeTransport t, string json, bool validSignature = true)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        var sig = validSignature ? Sign(bytes) : Sign(Encoding.UTF8.GetBytes("something else"));
        t.Responses[UpdateService.ManifestUrl] = bytes;
        t.Responses[UpdateService.SignatureUrl] = Encoding.UTF8.GetBytes(Convert.ToBase64String(sig));
        return bytes;
    }

    private (UpdateService svc, FakeTransport t, SettingsService settings, string dir) Make(
        string currentVersion = "6.6.2", string distribution = "Installer", bool demo = false)
    {
        var t = new FakeTransport();
        var dir = Path.Combine(Path.GetTempPath(), "nexus-updates-test-" + Path.GetRandomFileName());
        _tempDirs.Add(dir);
        var settings = new SettingsService(Path.Combine(dir, "settings.json"));
        var svc = new UpdateService(settings, t,
            (m, s) => UpdateVerifier.VerifySignature(m, s, Key.pub),
            () => distribution, currentVersion, Path.Combine(dir, "updates"), demo,
            swapper: new FakeSwapper(), purgeGuard: () => false);
        return (svc, t, settings, dir);
    }

    // Same as Make plus the process-start recorder seam, so a test never launches a real
    // process. The extra ctor parameter is optional, so Make's call above stays valid.
    private (UpdateService svc, FakeTransport t, SettingsService settings, string dir, List<string> started) Make2(
        string currentVersion = "6.6.2", string distribution = "Installer", bool demo = false)
    {
        var t = new FakeTransport();
        var dir = Path.Combine(Path.GetTempPath(), "nexus-updates-test-" + Path.GetRandomFileName());
        _tempDirs.Add(dir);
        var settings = new SettingsService(Path.Combine(dir, "settings.json"));
        var started = new List<string>();
        var svc = new UpdateService(settings, t,
            (m, s) => UpdateVerifier.VerifySignature(m, s, Key.pub),
            () => distribution, currentVersion, Path.Combine(dir, "updates"), demo,
            p => { started.Add(p); return true; }, swapper: new FakeSwapper(), purgeGuard: () => false);
        return (svc, t, settings, dir, started);
    }

    // Make2 plus the portable swapper seam and a deterministic purge guard.
    private (UpdateService svc, FakeTransport t, SettingsService settings, string dir, FakeSwapper swapper) Make3(
        string currentVersion = "6.6.2", string distribution = "Portable", bool demo = false,
        Func<bool>? purgeGuard = null)
    {
        var t = new FakeTransport();
        var dir = Path.Combine(Path.GetTempPath(), "nexus-updates-test-" + Path.GetRandomFileName());
        _tempDirs.Add(dir);
        var settings = new SettingsService(Path.Combine(dir, "settings.json"));
        var swapper = new FakeSwapper();
        var svc = new UpdateService(settings, t,
            (m, s) => UpdateVerifier.VerifySignature(m, s, Key.pub),
            () => distribution, currentVersion, Path.Combine(dir, "updates"), demo,
            p => true, swapper, purgeGuard ?? (() => false), () => @"C:\fake\NexusApp.exe");
        return (svc, t, settings, dir, swapper);
    }

    // Exactly what SettingsPage feeds the Updates status row, so a test reads the line the user
    // reads. The lastChecked stamp is irrelevant on a manual failure and never reached.
    private static string StatusOf(UpdateService svc) =>
        UpdateNotice.StatusLine(svc.State.ToString(), svc.Available?.Version, DateTime.UtcNow,
                                svc.LastFailureWasUserInitiated, svc.LastFailureKind.ToString());

    private static string HashOf(byte[] b) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(b)).ToLowerInvariant();

    [Fact]
    public async Task Check_NewerVersion_BecomesUpdateAvailable_AndStampsLastCheck()
    {
        var (svc, t, settings, _) = Make();
        Publish(t, UpdateManifestTests.ValidJson(version: "9.9.9"));
        await svc.CheckAsync(manual: true);
        Assert.Equal(UpdateState.UpdateAvailable, svc.State);
        Assert.Equal(new Version(9, 9, 9), svc.Available!.Version);
        Assert.NotNull(settings.Current.LastUpdateCheckUtc);
    }

    [Theory]
    [InlineData("6.6.2")]   // same
    [InlineData("6.0.0")]   // older
    public async Task Check_SameOrOlder_IsUpToDate(string v)
    {
        var (svc, t, _, _) = Make();
        Publish(t, UpdateManifestTests.ValidJson(version: v));
        await svc.CheckAsync(manual: true);
        Assert.Equal(UpdateState.UpToDate, svc.State);
        Assert.Null(svc.Available);
    }

    [Fact]
    public async Task Check_BadSignature_FailsClosed_NothingParsed()
    {
        var (svc, t, _, _) = Make();
        Publish(t, UpdateManifestTests.ValidJson(version: "9.9.9"), validSignature: false);
        await svc.CheckAsync(manual: true);
        Assert.Equal(UpdateState.Failed, svc.State);
        Assert.Null(svc.Available);
        Assert.Contains("signature", svc.LastFailure);
    }

    [Fact]
    public async Task Check_SignatureNotBase64_Fails()
    {
        var (svc, t, _, _) = Make();
        Publish(t, UpdateManifestTests.ValidJson(version: "9.9.9"));
        t.Responses[UpdateService.SignatureUrl] = Encoding.UTF8.GetBytes("@@not base64@@");
        await svc.CheckAsync(manual: true);
        Assert.Equal(UpdateState.Failed, svc.State);
    }

    [Fact]
    public async Task Check_ValidSignatureOverBadManifest_Fails()
    {
        // A correctly signed but structurally invalid manifest (no assets) still fails: passing
        // the signature check buys parsing, not trust in the contents. The defect here is the
        // empty asset list rather than a high schema, which is now the benign too-new branch
        // covered by Check_SchemaNewerThanThisBuild_ReadsAsUpToDate_NotFailed.
        var (svc, t, _, _) = Make();
        Publish(t, """{ "schema": 1, "version": "9.9.9", "assets": [] }""");
        await svc.CheckAsync(manual: true);
        Assert.Equal(UpdateState.Failed, svc.State);
    }

    [Fact]
    public async Task Check_SchemaNewerThanThisBuild_ReadsAsUpToDate_NotFailed()
    {
        // The bytes were signature-verified, so a higher schema means the publisher shipped a
        // manifest for a later Nexus. There is nothing this build can install, and telling the
        // user "update check failed" for a healthy release would be a lie.
        var (svc, t, _, _) = Make();
        Publish(t, UpdateManifestTests.ValidJson(version: "9.9.9", schema: 2));
        await svc.CheckAsync(manual: true);
        Assert.Equal(UpdateState.UpToDate, svc.State);
        Assert.Null(svc.Available);
        Assert.Null(svc.LastFailure);
    }

    [Fact]
    public async Task Check_SchemaBelowCurrent_StillFails()
    {
        // Only a schema ABOVE ours is the benign case. A zero or missing schema is a malformed
        // manifest and stays an ordinary failure.
        var (svc, t, _, _) = Make();
        Publish(t, UpdateManifestTests.ValidJson(version: "9.9.9", schema: 0));
        await svc.CheckAsync(manual: true);
        Assert.Equal(UpdateState.Failed, svc.State);
        Assert.Null(svc.Available);
        Assert.NotNull(svc.LastFailure);
    }

    [Fact]
    public async Task Check_TransportError_Fails_AndStillStampsLastCheck()
    {
        var (svc, t, settings, _) = Make();
        t.ThrowOnGet = new InvalidOperationException("dns exploded");
        await svc.CheckAsync(manual: false);
        Assert.Equal(UpdateState.Failed, svc.State);
        Assert.False(svc.LastFailureWasUserInitiated);
        Assert.NotNull(settings.Current.LastUpdateCheckUtc);
    }

    [Fact]
    public async Task Check_ManualFlag_IsRecordedOnFailure()
    {
        var (svc, t, _, _) = Make();
        t.ThrowOnGet = new InvalidOperationException("nope");
        await svc.CheckAsync(manual: true);
        Assert.True(svc.LastFailureWasUserInitiated);
    }

    // A raw "404 (Not Found)" in nexus.log needs the release layout to decode. These four pin the
    // plain-words classification instead: the log reason (LastFailure is the exact text logged
    // after the [UPDATE] tag) and the Settings line, for each cause the check can tell apart.
    [Fact]
    public async Task Check_ManifestMissing_ReadsAsNotSignedYet()
    {
        // Nothing published: the latest release exists but carries no update_manifest.json, which
        // is what an unsigned release looks like from here. Not a fault of the app or the network.
        var (svc, _, _, _) = Make();
        await svc.CheckAsync(manual: true);
        Assert.Equal(UpdateState.Failed, svc.State);
        Assert.Null(svc.Available);
        Assert.Equal(UpdateFailureKind.NotSignedYet, svc.LastFailureKind);
        Assert.Equal("check failed: the latest release is not signed yet (no update manifest published); updates stay hidden until it is signed",
            svc.LastFailure);
        Assert.Equal(UpdateNotice.CheckFailedNotSignedYet, StatusOf(svc));
    }

    [Fact]
    public async Task Check_SignatureMissing_ReadsAsIncompleteSigning()
    {
        // The manifest is up but the .sig never made it: signing ran halfway. Distinct from the
        // unsigned case because the publisher has a different thing to fix.
        var (svc, t, _, _) = Make();
        Publish(t, UpdateManifestTests.ValidJson(version: "9.9.9"));
        t.Responses.Remove(UpdateService.SignatureUrl);
        await svc.CheckAsync(manual: true);
        Assert.Equal(UpdateState.Failed, svc.State);
        Assert.Null(svc.Available);
        Assert.Equal(UpdateFailureKind.SignatureMissing, svc.LastFailureKind);
        Assert.Equal("check failed: the update manifest is present but its signature is missing (incomplete signing)",
            svc.LastFailure);
        Assert.Equal(UpdateNotice.CheckFailedSignatureMissing, StatusOf(svc));
    }

    [Theory]
    [InlineData(null)]                                 // DNS, TLS or timeout: no status at all
    [InlineData(HttpStatusCode.InternalServerError)]   // a real response, just not a usable one
    public async Task Check_NonNotFoundFailure_ReadsAsNetworkError(HttpStatusCode? status)
    {
        var (svc, t, _, _) = Make();
        t.ThrowOnGet = new HttpRequestException("No such host is known.", null, status);
        await svc.CheckAsync(manual: true);
        Assert.Equal(UpdateState.Failed, svc.State);
        Assert.Equal(UpdateFailureKind.Network, svc.LastFailureKind);
        Assert.Equal("check failed: network error: No such host is known.", svc.LastFailure);
        Assert.Equal(UpdateNotice.CheckFailedNetwork, StatusOf(svc));
    }

    [Fact]
    public async Task Check_SignatureDidNotVerify_MessageAndGenericLineAreUnchanged()
    {
        // The refusal path is untouched by the classification work: same reason text, no named
        // kind, and the generic line that sends the user to nexus.log.
        var (svc, t, _, _) = Make();
        Publish(t, UpdateManifestTests.ValidJson(version: "9.9.9"), validSignature: false);
        await svc.CheckAsync(manual: true);
        Assert.Equal(UpdateState.Failed, svc.State);
        Assert.Equal("check failed: the manifest signature did not verify", svc.LastFailure);
        Assert.Equal(UpdateFailureKind.None, svc.LastFailureKind);
        Assert.Equal(UpdateNotice.CheckFailed, StatusOf(svc));
    }

    [Fact]
    public async Task Check_DemoProfile_IsInert()
    {
        var (svc, t, _, _) = Make(demo: true);
        Publish(t, UpdateManifestTests.ValidJson(version: "9.9.9"));
        await svc.CheckAsync(manual: true);
        Assert.Equal(UpdateState.Idle, svc.State);
        Assert.Empty(t.Requested);
    }

    [Theory]
    [InlineData(null, false)]            // not asked: no auto-check
    [InlineData(false, false)]           // declined: no auto-check
    [InlineData(true, true)]             // enabled, never checked: check
    public void ShouldAutoCheck_ConsentMatrix(bool? enabled, bool expected) =>
        Assert.Equal(expected, UpdateService.ShouldAutoCheck(enabled, null, DateTime.UtcNow, isDemoProfile: false));

    [Fact]
    public void ShouldAutoCheck_ThrottleAndDemo()
    {
        var now = new DateTime(2026, 7, 25, 12, 0, 0, DateTimeKind.Utc);
        Assert.False(UpdateService.ShouldAutoCheck(true, now.AddHours(-23), now, false));  // inside 24h
        Assert.True(UpdateService.ShouldAutoCheck(true, now.AddHours(-25), now, false));   // outside 24h
        Assert.False(UpdateService.ShouldAutoCheck(true, null, now, true));                // demo: never
    }

    [Fact]
    public void ShouldAutoCheck_FutureStamp_SelfHeals()
    {
        // A clock rollback (or a stamp written while the clock was wrong) leaves lastCheck in
        // the future. Without the self-heal the elapsed span stays negative and auto-checks
        // never run again on that machine.
        var now = new DateTime(2026, 7, 25, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(UpdateService.ShouldAutoCheck(true, now.AddDays(400), now, false));
        Assert.True(UpdateService.ShouldAutoCheck(true, now.AddMinutes(1), now, false));
    }

    [Fact]
    public void AssetUrl_IsPinnedToTheVersionedReleasePath() =>
        Assert.Equal("https://github.com/T3SoD/NexusApp/releases/download/v6.7.0/Nexus_Setup.exe",
            UpdateService.AssetUrl(new Version(6, 7, 0), "Nexus_Setup.exe"));

    [Fact]
    public async Task Changed_FiresOnStateTransitions()
    {
        var (svc, t, _, _) = Make();
        Publish(t, UpdateManifestTests.ValidJson(version: "9.9.9"));
        int fired = 0;
        svc.Changed += () => fired++;
        await svc.CheckAsync(manual: true);
        Assert.True(fired >= 2);   // at least Checking then UpdateAvailable
    }

    [Fact]
    public async Task Changed_ThrowingSubscriber_DoesNotBreakTheCheck()
    {
        // A UI handler calling Dispatcher.Invoke during shutdown throws. That must never
        // fault the update flow or leave the state machine mid-transition.
        var (svc, t, _, _) = Make();
        Publish(t, UpdateManifestTests.ValidJson(version: "9.9.9"));
        svc.Changed += () => throw new InvalidOperationException("dispatcher is shutting down");
        await svc.CheckAsync(manual: true);
        Assert.Equal(UpdateState.UpdateAvailable, svc.State);
        Assert.Equal(new Version(9, 9, 9), svc.Available!.Version);
    }

    [Fact]
    public async Task Download_HappyPath_VerifiesAndBecomesReady()
    {
        var payload = Encoding.UTF8.GetBytes("the installer bytes");
        var (svc, t, _, dir, _) = Make2();
        Publish(t, UpdateManifestTests.ValidJson(version: "9.9.9", setupHash: HashOf(payload), setupSize: payload.Length));
        t.Files[UpdateService.AssetUrl(new Version(9, 9, 9), "Nexus_Setup.exe")] = payload;
        await svc.CheckAsync(manual: true);
        await svc.DownloadAsync();
        Assert.Equal(UpdateState.ReadyToInstall, svc.State);
        Assert.True(File.Exists(svc.DownloadedPath));
        Assert.EndsWith("Nexus_Setup.exe", svc.DownloadedPath);
        Assert.DoesNotContain(".partial", svc.DownloadedPath);
        Assert.Contains(Path.Combine(dir, "updates"), svc.DownloadedPath);
    }

    [Fact]
    public async Task Download_HashMismatch_DeletesFileAndFails()
    {
        var payload = Encoding.UTF8.GetBytes("the installer bytes");
        var (svc, t, _, dir, _) = Make2();
        // Manifest declares the right size but a WRONG hash: the classic swapped-asset attack.
        Publish(t, UpdateManifestTests.ValidJson(version: "9.9.9",
            setupHash: new string('c', 64), setupSize: payload.Length));
        t.Files[UpdateService.AssetUrl(new Version(9, 9, 9), "Nexus_Setup.exe")] = payload;
        await svc.CheckAsync(manual: true);
        await svc.DownloadAsync();
        Assert.Equal(UpdateState.Failed, svc.State);
        Assert.True(svc.LastFailureWasVerification);
        Assert.Null(svc.DownloadedPath);
        var updates = Path.Combine(dir, "updates");
        Assert.True(!Directory.Exists(updates) || Directory.GetFiles(updates, "*", SearchOption.AllDirectories).Length == 0);
    }

    [Fact]
    public async Task Download_SizeMismatch_Fails()
    {
        var payload = Encoding.UTF8.GetBytes("the installer bytes");
        var (svc, t, _, _, _) = Make2();
        Publish(t, UpdateManifestTests.ValidJson(version: "9.9.9", setupHash: HashOf(payload), setupSize: payload.Length + 5));
        t.Files[UpdateService.AssetUrl(new Version(9, 9, 9), "Nexus_Setup.exe")] = payload;
        await svc.CheckAsync(manual: true);
        await svc.DownloadAsync();
        Assert.Equal(UpdateState.Failed, svc.State);
    }

    [Fact]
    public async Task Download_PortableFlavor_FetchesThePortableZip()
    {
        var payload = Encoding.UTF8.GetBytes("zip bytes");
        var (svc, t, _, _, _) = Make2(distribution: "Portable");
        Publish(t, UpdateManifestTests.ValidJson(version: "9.9.9", portableHash: HashOf(payload), portableSize: payload.Length));
        t.Files[UpdateService.AssetUrl(new Version(9, 9, 9), "NexusApp_portable.zip")] = payload;
        await svc.CheckAsync(manual: true);
        await svc.DownloadAsync();
        Assert.Equal(UpdateState.ReadyToInstall, svc.State);
        Assert.EndsWith("NexusApp_portable.zip", svc.DownloadedPath);
    }

    // The UI subscribes to Changed and rebuilds the whole Operations page on every raise, so a
    // raise per read would mean roughly 1250 full rebuilds on a real download. DownloadedBytes
    // must still be exact on every report; only the raises collapse to whole-MB steps plus the
    // final byte (here: one MB boundary, one terminal report, plus the three state changes).
    [Fact]
    public async Task Download_ProgressRaisesAreThrottled_WhileDownloadedBytesStaysExact()
    {
        var payload = new byte[2_000_000];
        new Random(7).NextBytes(payload);
        var (svc, t, _, _, _) = Make2();
        Publish(t, UpdateManifestTests.ValidJson(version: "9.9.9", setupHash: HashOf(payload), setupSize: payload.Length));
        t.Files[UpdateService.AssetUrl(new Version(9, 9, 9), "Nexus_Setup.exe")] = payload;
        t.ProgressChunkBytes = 10_000;                  // 200 progress callbacks across the asset
        await svc.CheckAsync(manual: true);

        var raises = 0;
        svc.Changed += () => raises++;
        await svc.DownloadAsync();

        Assert.Equal(UpdateState.ReadyToInstall, svc.State);
        Assert.Equal(200, t.ProgressReports);
        Assert.Equal(payload.LongLength, svc.DownloadedBytes);   // every report still lands on the property
        Assert.True(raises <= 10, $"expected the throttle to collapse 200 reports into a handful of raises, saw {raises}");
    }

    [Fact]
    public async Task Download_WithoutUpdateAvailable_IsANoOp()
    {
        var (svc, _, _, _, _) = Make2();
        await svc.DownloadAsync();
        Assert.Equal(UpdateState.Idle, svc.State);
    }

    [Fact]
    public async Task LaunchInstaller_ReVerifiesThenStarts()
    {
        var payload = Encoding.UTF8.GetBytes("the installer bytes");
        var (svc, t, _, _, started) = Make2();
        Publish(t, UpdateManifestTests.ValidJson(version: "9.9.9", setupHash: HashOf(payload), setupSize: payload.Length));
        t.Files[UpdateService.AssetUrl(new Version(9, 9, 9), "Nexus_Setup.exe")] = payload;
        await svc.CheckAsync(manual: true);
        await svc.DownloadAsync();
        Assert.True(svc.LaunchInstaller());
        Assert.Equal(UpdateState.Installing, svc.State);
        Assert.Single(started);
        Assert.EndsWith("Nexus_Setup.exe", started[0]);
    }

    [Fact]
    public async Task LaunchInstaller_TamperedAfterDownload_RefusesAndDeletes()
    {
        var payload = Encoding.UTF8.GetBytes("the installer bytes");
        var (svc, t, _, _, started) = Make2();
        Publish(t, UpdateManifestTests.ValidJson(version: "9.9.9", setupHash: HashOf(payload), setupSize: payload.Length));
        t.Files[UpdateService.AssetUrl(new Version(9, 9, 9), "Nexus_Setup.exe")] = payload;
        await svc.CheckAsync(manual: true);
        await svc.DownloadAsync();
        File.WriteAllBytes(svc.DownloadedPath!, Encoding.UTF8.GetBytes("tampered"));   // verify-to-execute window
        Assert.False(svc.LaunchInstaller());
        Assert.Equal(UpdateState.Failed, svc.State);
        Assert.Empty(started);
        Assert.Null(svc.DownloadedPath);
    }

    [Fact]
    public void LaunchInstaller_WithoutDownload_IsFalse()
    {
        var (svc, _, _, _, started) = Make2();
        Assert.False(svc.LaunchInstaller());
        Assert.Empty(started);
    }

    [Fact]
    public async Task Download_CannotCreateTheFolder_FailsWithoutThrowing()
    {
        // A FILE sitting where the version directory needs to go makes CreateDirectory throw.
        // That must land in Failed, not escape as a faulted Task.
        var payload = Encoding.UTF8.GetBytes("the installer bytes");
        var (svc, t, _, dir, _) = Make2();
        Publish(t, UpdateManifestTests.ValidJson(version: "9.9.9", setupHash: HashOf(payload), setupSize: payload.Length));
        t.Files[UpdateService.AssetUrl(new Version(9, 9, 9), "Nexus_Setup.exe")] = payload;
        await svc.CheckAsync(manual: true);
        var updates = Path.Combine(dir, "updates");
        Directory.CreateDirectory(updates);
        File.WriteAllText(Path.Combine(updates, "9.9.9"), "in the way");
        await svc.DownloadAsync();                      // must not throw
        Assert.Equal(UpdateState.Failed, svc.State);
        Assert.Null(svc.DownloadedPath);
        Assert.False(svc.LastFailureWasVerification);   // a folder problem is not a tamper signal
        Assert.Empty(Directory.GetFiles(updates, "*.partial", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Download_CannotFinalizeTheRename_FailsAndCleansUpThePartial()
    {
        // A DIRECTORY sitting where the final file goes makes File.Move throw AFTER a
        // successful verify: the verified .partial must not be stranded on disk.
        var payload = Encoding.UTF8.GetBytes("the installer bytes");
        var (svc, t, _, dir, _) = Make2();
        Publish(t, UpdateManifestTests.ValidJson(version: "9.9.9", setupHash: HashOf(payload), setupSize: payload.Length));
        t.Files[UpdateService.AssetUrl(new Version(9, 9, 9), "Nexus_Setup.exe")] = payload;
        await svc.CheckAsync(manual: true);
        var updates = Path.Combine(dir, "updates");
        Directory.CreateDirectory(Path.Combine(updates, "9.9.9", "Nexus_Setup.exe"));
        await svc.DownloadAsync();                      // must not throw
        Assert.Equal(UpdateState.Failed, svc.State);
        Assert.Null(svc.DownloadedPath);
        Assert.False(svc.LastFailureWasVerification);   // the hash PASSED; only the rename broke
        Assert.Empty(Directory.GetFiles(updates, "*.partial", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task LaunchInstaller_PortableDistribution_RefusesWithoutDeletingTheDownload()
    {
        // Comparing the portable zip against the installer hash would "fail verification" and
        // delete a perfectly good download. A distribution mismatch is a caller bug: refuse quietly.
        var payload = Encoding.UTF8.GetBytes("zip bytes");
        var (svc, t, _, _, started) = Make2(distribution: "Portable");
        Publish(t, UpdateManifestTests.ValidJson(version: "9.9.9", portableHash: HashOf(payload), portableSize: payload.Length));
        t.Files[UpdateService.AssetUrl(new Version(9, 9, 9), "NexusApp_portable.zip")] = payload;
        await svc.CheckAsync(manual: true);
        await svc.DownloadAsync();
        var downloaded = svc.DownloadedPath!;
        Assert.False(svc.LaunchInstaller());
        Assert.True(File.Exists(downloaded));
        Assert.Equal(UpdateState.ReadyToInstall, svc.State);
        Assert.False(svc.LastFailureWasVerification);
        Assert.Empty(started);
    }

    [Fact]
    public void PurgeStaleDownloads_EmptiesTheUpdatesFolder()
    {
        var (svc, _, _, dir, _) = Make2();
        var updates = Path.Combine(dir, "updates");
        Directory.CreateDirectory(Path.Combine(updates, "9.9.9"));
        File.WriteAllText(Path.Combine(updates, "9.9.9", "old.partial"), "junk");
        svc.PurgeStaleDownloads();
        Assert.False(Directory.Exists(updates));
    }

    // ---- Portable self-swap state machine ----

    // Both assets are published and served so the helper reaches ReadyToInstall for EITHER
    // distribution: the installer variant exists to prove ApplyPortableAsync refuses from a
    // healthy ReadyToInstall, which a 404'd download would hide behind a Failed state.
    private async Task<(UpdateService svc, FakeSwapper swapper)> ReadyPortable(
        string distribution = "Portable", Func<bool>? purgeGuard = null)
    {
        var (svc, t, _, _, swapper) = Make3(distribution: distribution, purgeGuard: purgeGuard);
        var payload = Encoding.UTF8.GetBytes("zip bytes");
        var setup = Encoding.UTF8.GetBytes("setup bytes");
        Publish(t, UpdateManifestTests.ValidJson(version: "9.9.9",
            setupHash: HashOf(setup), setupSize: setup.Length,
            portableHash: HashOf(payload), portableSize: payload.Length));
        t.Files[UpdateService.AssetUrl(new Version(9, 9, 9), "NexusApp_portable.zip")] = payload;
        t.Files[UpdateService.AssetUrl(new Version(9, 9, 9), "Nexus_Setup.exe")] = setup;
        await svc.CheckAsync(manual: true);
        await svc.DownloadAsync();
        return (svc, swapper);
    }

    [Fact]
    public async Task Download_Portable_EvaluatesPreflightAndPublishesAvailability()
    {
        var (svc, swapper) = await ReadyPortable();
        Assert.Equal(UpdateState.ReadyToInstall, svc.State);
        Assert.Equal(1, swapper.PreflightCalls);
        Assert.True(svc.PortableSwapAvailable);
        Assert.Null(svc.PortableSwapUnavailableReason);
    }

    [Fact]
    public async Task Download_PortablePreflightFails_OffersManualWithReason()
    {
        var (svc, t, _, _, swapper) = Make3();
        swapper.PreflightResult = new PortablePreflight(false, "the app file has been renamed");
        var payload = Encoding.UTF8.GetBytes("zip bytes");
        Publish(t, UpdateManifestTests.ValidJson(version: "9.9.9", portableHash: HashOf(payload), portableSize: payload.Length));
        t.Files[UpdateService.AssetUrl(new Version(9, 9, 9), "NexusApp_portable.zip")] = payload;
        await svc.CheckAsync(manual: true);
        await svc.DownloadAsync();
        Assert.Equal(UpdateState.ReadyToInstall, svc.State);
        Assert.False(svc.PortableSwapAvailable);
        Assert.Equal("the app file has been renamed", svc.PortableSwapUnavailableReason);
    }

    [Fact]
    public async Task Download_Installer_NeverTouchesThePreflight()
    {
        var (svc, t, _, _, swapper) = Make3(distribution: "Installer");
        var payload = Encoding.UTF8.GetBytes("setup bytes");
        Publish(t, UpdateManifestTests.ValidJson(version: "9.9.9", setupHash: HashOf(payload), setupSize: payload.Length));
        t.Files[UpdateService.AssetUrl(new Version(9, 9, 9), "Nexus_Setup.exe")] = payload;
        await svc.CheckAsync(manual: true);
        await svc.DownloadAsync();
        Assert.Equal(0, swapper.PreflightCalls);
        Assert.False(svc.PortableSwapAvailable);
    }

    [Fact]
    public async Task Download_PendingJournal_OffersManualWithoutProbing()
    {
        // A pending journal means Apply will refuse anyway: the strip must route to the guided
        // manual flow rather than an Install button whose Try again cannot work until restart.
        var (svc, t, _, _, swapper) = Make3(purgeGuard: () => true);
        var payload = Encoding.UTF8.GetBytes("zip bytes");
        Publish(t, UpdateManifestTests.ValidJson(version: "9.9.9", portableHash: HashOf(payload), portableSize: payload.Length));
        t.Files[UpdateService.AssetUrl(new Version(9, 9, 9), "NexusApp_portable.zip")] = payload;
        await svc.CheckAsync(manual: true);
        await svc.DownloadAsync();
        Assert.Equal(UpdateState.ReadyToInstall, svc.State);
        Assert.False(svc.PortableSwapAvailable);
        Assert.Contains("previous update", svc.PortableSwapUnavailableReason);
        Assert.Equal(0, swapper.PreflightCalls);
    }

    [Fact]
    public async Task Download_PreflightThrows_StillLandsReadyWithManualFlow()
    {
        // The one place an engine exception could strand a verified download short of
        // ReadyToInstall: it must land in the manual flow instead, with the cause logged.
        var (svc, t, _, _, swapper) = Make3();
        swapper.PreflightThrows = new InvalidOperationException("boom");
        var payload = Encoding.UTF8.GetBytes("zip bytes");
        Publish(t, UpdateManifestTests.ValidJson(version: "9.9.9", portableHash: HashOf(payload), portableSize: payload.Length));
        t.Files[UpdateService.AssetUrl(new Version(9, 9, 9), "NexusApp_portable.zip")] = payload;
        await svc.CheckAsync(manual: true);
        await svc.DownloadAsync();
        Assert.Equal(UpdateState.ReadyToInstall, svc.State);
        Assert.False(svc.PortableSwapAvailable);
        Assert.NotNull(svc.PortableSwapUnavailableReason);
    }

    [Fact]
    public async Task ApplyPortable_HappyPath_LandsInInstallingWithRelaunchPending()
    {
        var (svc, swapper) = await ReadyPortable();
        Assert.True(await svc.ApplyPortableAsync());
        Assert.Equal(1, swapper.ApplyCalls);
        Assert.Equal(UpdateState.Installing, svc.State);
        Assert.True(svc.PortableApplyInProgress);
        Assert.Equal(@"C:\fake\NexusApp.exe", svc.PendingRelaunchPath);
        Assert.Equal("Nexus 9.9.9 is available", StatusOf(svc));   // Installing keeps the available line
    }

    [Fact]
    public async Task ApplyPortable_Failure_ReturnsToReadyWithTheReason()
    {
        var (svc, swapper) = await ReadyPortable();
        swapper.ApplyResult = new PortableApplyResult(PortableApplyOutcome.FailedRolledBack, "e_sqlite3.dll is held open by another program");
        Assert.False(await svc.ApplyPortableAsync());
        Assert.Equal(UpdateState.ReadyToInstall, svc.State);
        Assert.False(svc.PortableApplyInProgress);
        Assert.Null(svc.PendingRelaunchPath);
        Assert.Equal("e_sqlite3.dll is held open by another program", svc.LastPortableApplyFailure);
        // A later successful attempt must clear the note.
        swapper.ApplyResult = new PortableApplyResult(PortableApplyOutcome.Completed, "");
        Assert.True(await svc.ApplyPortableAsync());
        Assert.Null(svc.LastPortableApplyFailure);
    }

    [Fact]
    public async Task ApplyPortable_RollbackIncomplete_FlagsTheRestoreAsPending()
    {
        // The stuck-rollback state drives different copy and drops the Try again button: the
        // previous version can only be put back by the next start's recovery.
        var (svc, swapper) = await ReadyPortable();
        swapper.ApplyResult = new PortableApplyResult(PortableApplyOutcome.FailedRollbackIncomplete,
            "e_sqlite3.dll changed between unpack and install");
        Assert.False(await svc.ApplyPortableAsync());
        Assert.Equal(UpdateState.ReadyToInstall, svc.State);
        Assert.True(svc.LastApplyLeftRestorePending);
        Assert.NotNull(svc.LastPortableApplyFailure);
        // An ordinary rollback is not the pending state, and a later run clears the flag.
        swapper.ApplyResult = new PortableApplyResult(PortableApplyOutcome.FailedRolledBack, "held open by another program");
        Assert.False(await svc.ApplyPortableAsync());
        Assert.False(svc.LastApplyLeftRestorePending);
    }

    [Fact]
    public async Task ApplyPortable_InstallerDistribution_Refuses()
    {
        var (svc, swapper) = await ReadyPortable(distribution: "Installer");
        Assert.False(await svc.ApplyPortableAsync());
        Assert.Equal(0, swapper.ApplyCalls);
        Assert.Equal(UpdateState.ReadyToInstall, svc.State);
    }

    [Fact]
    public async Task ApplyPortable_WrongState_Refuses()
    {
        var (svc, _, _, _, swapper) = Make3();
        Assert.False(await svc.ApplyPortableAsync());   // Idle: nothing downloaded
        Assert.Equal(0, swapper.ApplyCalls);
    }

    [Fact]
    public async Task ApplyPortable_Demo_IsInert()
    {
        var (svc, _, _, _, swapper) = Make3(demo: true);
        Assert.False(await svc.ApplyPortableAsync());
        Assert.Equal(0, swapper.ApplyCalls);
    }

    [Fact]
    public async Task UnpackForManual_Success_LandsInManualHandoff()
    {
        var (svc, swapper) = await ReadyPortable();
        await svc.UnpackForManualAsync();
        Assert.Equal(1, swapper.UnpackCalls);
        Assert.Equal(UpdateState.ManualHandoff, svc.State);
        Assert.Equal("Nexus 9.9.9 is available", StatusOf(svc));
    }

    [Fact]
    public async Task UnpackForManual_Failure_ReturnsToReadyWithNote()
    {
        var (svc, swapper) = await ReadyPortable();
        swapper.UnpackResult = false;
        await svc.UnpackForManualAsync();
        Assert.Equal(UpdateState.ReadyToInstall, svc.State);
        Assert.NotNull(svc.LastPortableApplyFailure);
    }

    [Fact]
    public async Task UnpackForManual_AfterAStuckRollback_ClearsTheRestorePendingFlag()
    {
        // The manual unpack is its own attempt: its failure must regain the Try again
        // affordance instead of keeping the earlier swap's restart-to-restore copy.
        var (svc, swapper) = await ReadyPortable();
        swapper.ApplyResult = new PortableApplyResult(PortableApplyOutcome.FailedRollbackIncomplete, "rollback incomplete at e_sqlite3.dll");
        Assert.False(await svc.ApplyPortableAsync());
        Assert.True(svc.LastApplyLeftRestorePending);
        swapper.UnpackResult = false;
        await svc.UnpackForManualAsync();
        Assert.Equal(UpdateState.ReadyToInstall, svc.State);
        Assert.False(svc.LastApplyLeftRestorePending);
        Assert.NotNull(svc.LastPortableApplyFailure);
    }

    [Fact]
    public async Task PreferManualUpdate_ForcesTheManualFlow()
    {
        var (svc, t, _, _, swapper) = Make3();
        svc.PreferManualUpdate = true;
        var payload = Encoding.UTF8.GetBytes("zip bytes");
        Publish(t, UpdateManifestTests.ValidJson(version: "9.9.9", portableHash: HashOf(payload), portableSize: payload.Length));
        t.Files[UpdateService.AssetUrl(new Version(9, 9, 9), "NexusApp_portable.zip")] = payload;
        await svc.CheckAsync(manual: true);
        await svc.DownloadAsync();
        Assert.False(svc.PortableSwapAvailable);
        Assert.Equal(0, swapper.PreflightCalls);   // the choice is honored without probing
    }

    [Fact]
    public void PurgeStaleDownloads_SkipsWhileAJournalExists()
    {
        var (svc, _, _, dir, _) = Make3(purgeGuard: () => true);
        var updates = Path.Combine(dir, "updates");
        Directory.CreateDirectory(Path.Combine(updates, "9.9.9"));
        File.WriteAllText(Path.Combine(updates, "9.9.9", "NexusApp_portable.zip"), "the recovery artifact");
        svc.PurgeStaleDownloads();
        Assert.True(File.Exists(Path.Combine(updates, "9.9.9", "NexusApp_portable.zip")));
    }
}
