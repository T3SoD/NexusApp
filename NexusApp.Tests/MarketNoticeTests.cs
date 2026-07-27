using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// The pure seam every market data surface reads its strings and show/hide decisions from.
// Testing it headless pins the copy (voice rules: no exclamation marks, no em-dashes)
// and the consent visibility logic.
public class MarketNoticeTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    public void Dispose()
    {
        foreach (var d in _tempDirs)
        {
            try { if (Directory.Exists(d)) Directory.Delete(d, recursive: true); }
            catch { /* best effort */ }
        }
    }

    private string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nexus-settings-test-" + Path.GetRandomFileName());
        _tempDirs.Add(dir);
        return dir;
    }

    [Theory]
    [InlineData(null, false, true)]   // not asked yet: show the one-time strip
    [InlineData(null, true, false)]   // demo profile: never
    [InlineData(true, false, false)]  // answered: never again
    [InlineData(false, false, false)]
    public void ShouldShowConsent_Matrix(bool? enabled, bool demo, bool expected) =>
        Assert.Equal(expected, MarketNotice.ShouldShowConsent(enabled, demo));

    [Fact]
    public void FormatAge_30Seconds_JustNow() =>
        Assert.Equal("just now", MarketNotice.FormatAge(TimeSpan.FromSeconds(30)));

    [Fact]
    public void FormatAge_12Minutes_ShowsMinutes() =>
        Assert.Equal("12m ago", MarketNotice.FormatAge(TimeSpan.FromMinutes(12)));

    [Fact]
    public void FormatAge_3Hours_ShowsHours() =>
        Assert.Equal("3h ago", MarketNotice.FormatAge(TimeSpan.FromHours(3)));

    [Fact]
    public void FormatAge_2Days_ShowsDays() =>
        Assert.Equal("2d ago", MarketNotice.FormatAge(TimeSpan.FromDays(2)));

    [Fact]
    public void FormatAge_Negative_JustNow() =>
        Assert.Equal("just now", MarketNotice.FormatAge(TimeSpan.FromSeconds(-1)));

    [Fact]
    public void PatchTag_FormatsVersion() =>
        Assert.Equal("patch 4.8", MarketNotice.PatchTag("4.8"));

    [Fact]
    public void DecoderLine_FormatsCorrectly() =>
        Assert.Equal("Sell (raw, avg): 8,210/SCU at Refinery Ore Sales - ARC-L1 (12m ago)",
            MarketNotice.DecoderLine(8210, "Refinery Ore Sales - ARC-L1", "12m ago"));

    [Fact]
    public void StatusLine_NeverFetched() =>
        Assert.Equal("Never refreshed", MarketNotice.StatusLine(null, null));

    [Fact]
    public void StatusLine_Success() =>
        Assert.Equal("Last refresh: 12:00",
            MarketNotice.StatusLine(new DateTime(2026, 7, 25, 12, 0, 0, DateTimeKind.Local), null));

    [Fact]
    public void StatusLine_WithError() =>
        Assert.Equal("Last refresh: 12:00 (timeout)",
            MarketNotice.StatusLine(new DateTime(2026, 7, 25, 12, 0, 0, DateTimeKind.Local), "timeout"));

    [Fact]
    public void Bodies_ContainNoBannedPunctuation()
    {
        var all = new[]
        {
            MarketNotice.ConsentEyebrow, MarketNotice.ConsentBody,
            MarketNotice.ConsentEnable, MarketNotice.ConsentDecline,
            MarketNotice.SettingsTitle, MarketNotice.SettingsToggleTitle,
            MarketNotice.SettingsToggleDesc,
            MarketNotice.RefreshNow, MarketNotice.SourceNote,
            MarketNotice.DossierFooter, MarketNotice.DossierSection,
            MarketNotice.NeverFetched,
            MarketNotice.PatchTag("4.8"),
            MarketNotice.FormatAge(TimeSpan.FromMinutes(12)),
            MarketNotice.DecoderLine(8210, "Terminal", "12m ago"),
            MarketNotice.StatusLine(DateTime.Now, null),
            MarketNotice.StatusLine(DateTime.Now, "error"),
            MarketNotice.SnapshotAgeNote(TimeSpan.FromMinutes(30)),
        };
        foreach (var s in all)
        {
            Assert.DoesNotContain("!", s);
            Assert.DoesNotContain("\u2014", s);   // the em-dash ban is a standing rule; escaped so the character itself never enters the repo
        }
    }

    [Fact]
    public void SettingsRoundTrip_MarketDataPropertiesPersistAndDefaultNull()
    {
        var path = Path.Combine(TempDir(), "settings.json");
        try
        {
            var svc = new SettingsService(path);
            Assert.Null(svc.Current.MarketDataEnabled);
            Assert.Null(svc.Current.LastMarketFetchUtc);
            svc.Current.MarketDataEnabled = true;
            svc.Current.LastMarketFetchUtc = new DateTime(2026, 7, 25, 12, 0, 0, DateTimeKind.Utc);
            svc.Save();
            var again = new SettingsService(path);
            Assert.True(again.Current.MarketDataEnabled);
            Assert.NotNull(again.Current.LastMarketFetchUtc);
            Assert.Equal(new DateTime(2026, 7, 25, 12, 0, 0, DateTimeKind.Utc), again.Current.LastMarketFetchUtc);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
