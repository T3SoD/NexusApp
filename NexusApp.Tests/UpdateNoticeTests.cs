using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// The pure seam every update surface reads its strings and show/hide decisions from.
// Testing it headless pins the copy (voice rules: no exclamation marks, "Couldn't ...")
// and the consent/post-update visibility logic.
public class UpdateNoticeTests
{
    [Theory]
    [InlineData(null, false, true)]   // not asked yet: show the one-time strip
    [InlineData(null, true, false)]   // demo profile: never
    [InlineData(true, false, false)]  // answered: never again
    [InlineData(false, false, false)]
    public void ShouldShowConsentStrip_Matrix(bool? enabled, bool demo, bool expected) =>
        Assert.Equal(expected, UpdateNotice.ShouldShowConsentStrip(enabled, demo));

    [Theory]
    [InlineData(null, "6.7.0", false)]      // fresh install: nothing to announce
    [InlineData("6.6.2", "6.7.0", true)]    // ran 6.6.2 last time, now 6.7.0: announce
    [InlineData("6.7.0", "6.7.0", false)]   // same version: quiet
    [InlineData("6.8.0", "6.7.0", false)]   // downgrade (manual): quiet
    [InlineData("garbage", "6.7.0", false)]
    public void ShouldShowPostUpdateStrip_Matrix(string? lastSeen, string current, bool expected) =>
        Assert.Equal(expected, UpdateNotice.ShouldShowPostUpdateStrip(lastSeen, current));

    [Fact]
    public void FormatLastChecked_EmptyState() =>
        Assert.Equal(UpdateNotice.NeverChecked, UpdateNotice.FormatLastChecked(null));

    [Fact]
    public void FormatLastChecked_RendersLocalTimestamp() =>
        Assert.StartsWith("Last checked ", UpdateNotice.FormatLastChecked(new DateTime(2026, 7, 25, 12, 0, 0, DateTimeKind.Utc)));

    [Fact]
    public void StatusLine_FailedManual_ShowsCouldnt() =>
        Assert.Equal(UpdateNotice.CheckFailed, UpdateNotice.StatusLine("Failed", null, DateTime.UtcNow, lastFailureWasUserInitiated: true));

    [Fact]
    public void StatusLine_FailedBackground_StaysQuiet() =>
        // Background failures stay out of the UI: the row falls back to the last-checked stamp.
        Assert.StartsWith("Last checked ", UpdateNotice.StatusLine("Failed", null, DateTime.UtcNow, lastFailureWasUserInitiated: false));

    [Fact]
    public void StatusLine_UpToDate() =>
        Assert.Equal("Up to date", UpdateNotice.StatusLine("UpToDate", null, null, false));

    [Theory]
    [InlineData("UpdateAvailable")]
    [InlineData("Downloading")]
    [InlineData("Verifying")]
    [InlineData("ReadyToInstall")]
    [InlineData("Installing")]
    [InlineData("ManualHandoff")]
    public void StatusLine_AvailableStates_NameTheVersion(string state) =>
        Assert.Equal("Nexus 9.9.9 is available", UpdateNotice.StatusLine(state, new Version(9, 9, 9), null, false));

    [Fact]
    public void StatusLine_Idle_FallsBackToLastChecked() =>
        Assert.Equal(UpdateNotice.NeverChecked, UpdateNotice.StatusLine("Idle", null, null, false));

    [Fact]
    public void Bodies_ContainNoBannedPunctuation()
    {
        var all = new[]
        {
            UpdateNotice.ConsentBody, UpdateNotice.CheckFailed, UpdateNotice.VerifyFailedBody,
            UpdateNotice.CheckFailedNotSignedYet, UpdateNotice.CheckFailedSignatureMissing,
            UpdateNotice.CheckFailedNetwork,
            UpdateNotice.UpdateBody("6.6.2", new Version(6, 7, 0)),
            UpdateNotice.DownloadingBody(new Version(6, 7, 0), 5 * 1048576, 100 * 1048576),
            UpdateNotice.VerifyingBody(new Version(6, 7, 0)),
            UpdateNotice.ReadyBodyInstaller(new Version(6, 7, 0)),
            UpdateNotice.ReadyBodyPortable(new Version(6, 7, 0)),
            UpdateNotice.PostUpdateBody("6.7.0"),
            UpdateNotice.InstallConfirmBodyPortable,
            UpdateNotice.PrepareFailedBody,
            UpdateNotice.RestorePendingBody,
            UpdateNotice.PreparingBody(new Version(6, 9, 0)),
            UpdateNotice.ReadyBodyPortableManual(new Version(6, 9, 0)),
            UpdateNotice.UnpackingBody(new Version(6, 9, 0)),
            UpdateNotice.ManualHandoffBody(new Version(6, 9, 0)),
            UpdateNotice.SwapFailedBody("6.9.0", "6.8.1"),
        };
        foreach (var s in all)
        {
            Assert.DoesNotContain("!", s);
            Assert.DoesNotContain("\u2014", s);   // the em-dash ban is a standing rule; escaped so the character itself never enters the repo
        }
    }

    [Fact]
    public void DownloadingBody_RoundsToWholeMegabytes() =>
        Assert.Equal("Downloading Nexus 6.7.0. 5 of 100 MB.", UpdateNotice.DownloadingBody(new Version(6, 7, 0), 5 * 1048576, 100 * 1048576));

    [Fact]
    public void SettingsRoundTrip_NewPropertiesPersistAndDefaultNull()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        try
        {
            var svc = new SettingsService(path);
            Assert.Null(svc.Current.UpdateCheckEnabled);
            Assert.Null(svc.Current.LastUpdateCheckUtc);
            Assert.Null(svc.Current.LastSeenVersion);
            svc.Current.UpdateCheckEnabled = true;
            svc.Current.LastUpdateCheckUtc = new DateTime(2026, 7, 25, 12, 0, 0, DateTimeKind.Utc);
            svc.Current.LastSeenVersion = "6.6.2";
            svc.Save();
            var again = new SettingsService(path);
            Assert.True(again.Current.UpdateCheckEnabled);
            Assert.Equal("6.6.2", again.Current.LastSeenVersion);
            Assert.NotNull(again.Current.LastUpdateCheckUtc);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ReadyBodyPortable_MatchesInstallerBody() =>
        // Installer parity: the portable user should not need distribution vocabulary.
        Assert.Equal(UpdateNotice.ReadyBodyInstaller(new Version(6, 9, 0)),
                     UpdateNotice.ReadyBodyPortable(new Version(6, 9, 0)));

    [Fact]
    public void PortableSwapBodies_PinExactCopy()
    {
        Assert.Equal("Nexus will close for a moment and reopen as the new version. Your settings, work orders, and blueprints are kept.",
            UpdateNotice.InstallConfirmBodyPortable);
        Assert.Equal("Preparing Nexus 6.9.0. Nexus will close and reopen in a moment.",
            UpdateNotice.PreparingBody(new Version(6, 9, 0)));
        Assert.Equal("Nexus 6.9.0 is downloaded and verified. Nexus cannot replace its own files from this location, so this update finishes with one quick copy.",
            UpdateNotice.ReadyBodyPortableManual(new Version(6, 9, 0)));
        Assert.Equal("Unpacking Nexus 6.9.0.",
            UpdateNotice.UnpackingBody(new Version(6, 9, 0)));
        Assert.Equal("Two folders are open: the new Nexus 6.9.0 and your current Nexus. Close Nexus, then copy everything from the new folder into the current one, replacing files when asked.",
            UpdateNotice.ManualHandoffBody(new Version(6, 9, 0)));
        Assert.Equal("Couldn't prepare the update. Nothing was changed. Try again, or update manually from Settings > Diagnostics.",
            UpdateNotice.PrepareFailedBody);
        Assert.Equal("The update could not finish. Nexus will finish restoring the previous version the next time it starts. Close Nexus and start it again.",
            UpdateNotice.RestorePendingBody);
        Assert.Equal("The update to Nexus 6.9.0 could not finish. Nexus restored the previous version and nothing changed. You are still on Nexus 6.8.1.",
            UpdateNotice.SwapFailedBody("6.9.0", "6.8.1"));
    }
}
