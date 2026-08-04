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

    // Every rendered price carries the game's currency unit, "aUEC/SCU" (owner ruling 2026-07-27).
    [Fact]
    public void DecoderLine_FormatsCorrectly() =>
        Assert.Equal("Sell (refined, avg): 8,210 aUEC/SCU at Refinery Ore Sales - ARC-L1 (12m ago)",
            MarketNotice.DecoderLine(8210, "Refinery Ore Sales - ARC-L1", "12m ago"));

    // The overlay card line drops the "(refined, avg)" qualifier the app-window surfaces carry
    // (the card has no room for it) but keeps the value with its unit, the terminal and the age.
    [Fact]
    public void OverlaySellLine_FormatsCorrectly() =>
        Assert.Equal("Sell: 8,210 aUEC/SCU at Refinery Ore Sales - ARC-L1 (12m ago)",
            MarketNotice.OverlaySellLine(8210, "Refinery Ore Sales - ARC-L1", "12m ago"));

    // Stale rows show the patch tag where a fresh row shows its age, so a price never renders bare.
    [Fact]
    public void OverlaySellLine_AcceptsPatchTagAsAgeText() =>
        Assert.Equal("Sell: 1,000 aUEC/SCU at Terminal (patch 4.8)",
            MarketNotice.OverlaySellLine(1000, "Terminal", MarketNotice.PatchTag("4.8")));

    // The unit is part of the copy contract on both lines: a value must never render bare.
    [Fact]
    public void PriceLines_CarryTheCurrencyUnit()
    {
        Assert.Contains("aUEC/SCU", MarketNotice.DecoderLine(8210, "Terminal", "12m ago"));
        Assert.Contains("aUEC/SCU", MarketNotice.OverlaySellLine(8210, "Terminal", "12m ago"));
    }

    // The dossier hero's twin of DecoderLine: same number, same never-a-bare-price rule, the
    // dossier's own "Best sell" voice because the VALUE section below it lists the runners-up.
    [Fact]
    public void DossierHeroLine_FormatsCorrectly() =>
        Assert.Equal("Best sell: 34,500 aUEC/SCU at Sacren's Plot (2h ago)",
            MarketNotice.DossierHeroLine(34500, "Sacren's Plot", "2h ago"));

    [Fact]
    public void DossierHeroLine_AcceptsPatchTagAsAgeText() =>
        Assert.Equal("Best sell: 1,000 aUEC/SCU at Terminal (patch 4.8)",
            MarketNotice.DossierHeroLine(1000, "Terminal", MarketNotice.PatchTag("4.8")));

    // An afternoon hour past 12, so the assertion actually distinguishes 24 hour from 12 hour.
    // The one-line sell surfaces render segmented (label dim, value gold, terminal Fg, age dim),
    // and each renderer builds its runs from these parts. If a formatter ever stopped being
    // composed from them, the rendered line and the pinned string would drift apart silently, so
    // the composition is asserted for all three lines.
    [Theory]
    [InlineData("decoder")]
    [InlineData("overlay")]
    [InlineData("dossier")]
    public void SellLines_AreComposedFromTheirParts(string surface)
    {
        var (label, line) = surface switch
        {
            "decoder" => (MarketNotice.DecoderLabel, MarketNotice.DecoderLine(8210, "Terminal", "12m ago")),
            "overlay" => (MarketNotice.OverlayLabel, MarketNotice.OverlaySellLine(8210, "Terminal", "12m ago")),
            _         => (MarketNotice.DossierHeroLabel, MarketNotice.DossierHeroLine(8210, "Terminal", "12m ago")),
        };
        var parts = string.Join(" ", label, MarketNotice.PriceValue(8210),
                                MarketNotice.AtTerminal("Terminal"), MarketNotice.AgePart("12m ago"));
        Assert.Equal(line, parts);
    }

    [Fact]
    public void SellLineParts_CarryUnitTerminalAndAge()
    {
        Assert.Equal("8,210 aUEC/SCU", MarketNotice.PriceValue(8210));
        Assert.Equal("at Sacren's Plot", MarketNotice.AtTerminal("Sacren's Plot"));
        Assert.Equal("(patch 4.8)", MarketNotice.AgePart(MarketNotice.PatchTag("4.8")));
    }

    // ── PillState: one value grammar for both TRADE DATA pills (owner, 2026-08-04) ──
    // The header chip renders the same hours-since text the Trade page strip pill composes from
    // FormatAge, so the two pills can never read as different facts again. The old fresh/busy
    // clock (PillClock) and the stale-only compact age (PillAge) are gone with it.

    [Fact]
    public void PillState_Fresh_ShowsHoursSinceUpdate() =>
        Assert.Equal(("fresh", "3h ago", MarketNotice.PillTooltip),
            MarketNotice.PillState(busy: false, lastError: null, age: TimeSpan.FromHours(3)));

    [Fact]
    public void PillState_FreshUnderAMinute_JustNow() =>
        Assert.Equal("just now", MarketNotice.PillState(false, null, TimeSpan.FromSeconds(30)).Text);

    [Fact]
    public void PillState_Stale_SameGrammarDifferentState() =>
        Assert.Equal(("stale", "1d ago", MarketNotice.PillTooltip),
            MarketNotice.PillState(false, null, TimeSpan.FromHours(26)));

    [Fact]
    public void PillState_BusyWithPriorData_KeepsTheAge() =>
        Assert.Equal(("busy", "2h ago", MarketNotice.PillTooltip),
            MarketNotice.PillState(true, null, TimeSpan.FromHours(2)));

    [Fact]
    public void PillState_BusyFirstEver_Syncing() =>
        Assert.Equal(("busy", MarketNotice.PillSyncing, MarketNotice.PillTooltip),
            MarketNotice.PillState(true, null, null));

    // A refresh in flight is the most current fact about the channel, so busy outranks the
    // previous cycle's error (which comes back by itself if this cycle fails too).
    [Fact]
    public void PillState_BusyOutranksError() =>
        Assert.Equal("busy", MarketNotice.PillState(true, "timeout", TimeSpan.FromHours(2)).State);

    [Fact]
    public void PillState_Error_OfflineWithReasonAsTip() =>
        Assert.Equal(("error", MarketNotice.PillOffline, "timeout"),
            MarketNotice.PillState(false, "timeout", TimeSpan.FromHours(1)));

    [Fact]
    public void PillState_NeverFetched_NoData() =>
        Assert.Equal(("nodata", MarketNotice.PillNoData, MarketNotice.PillTooltip),
            MarketNotice.PillState(false, null, null));

    // The parity lock: at any age, the header chip's value is byte-identical to what the Trade
    // page strip pill renders (TradePage.RefreshContextRow, FormatAge).
    [Theory]
    [InlineData(0.5)]
    [InlineData(45)]
    [InlineData(60 * 5)]
    [InlineData(60 * 26)]
    public void PillState_ValueMirrorsFormatAge(double minutes)
    {
        var age = TimeSpan.FromMinutes(minutes);
        Assert.Equal(MarketNotice.FormatAge(age), MarketNotice.PillState(false, null, age).Text);
    }

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
            MarketNotice.RefreshNow, MarketNotice.SourceNote, MarketNotice.CadenceNote,
            MarketNotice.DossierFooter, MarketNotice.ValueSection,
            MarketNotice.ValueDetailsShow, MarketNotice.ValueDetailsHide,
            MarketNotice.BestRefineryLabel, MarketNotice.CodexColumnToggle,
            MarketNotice.DecoderLabel, MarketNotice.OverlayLabel, MarketNotice.DossierHeroLabel,
            MarketNotice.PriceValue(8210), MarketNotice.AtTerminal("Terminal"),
            MarketNotice.AgePart("12m ago"),
            MarketNotice.PillLabel, MarketNotice.PillOffline, MarketNotice.PillSyncing,
            MarketNotice.PillNoData, MarketNotice.PillTooltip,
            MarketNotice.PillState(false, null, TimeSpan.FromHours(26)).Text,
            MarketNotice.PillState(true, null, null).Text,
            MarketNotice.NeverFetched,
            MarketNotice.PatchTag("4.8"),
            MarketNotice.FormatAge(TimeSpan.FromMinutes(12)),
            MarketNotice.DecoderLine(8210, "Terminal", "12m ago"),
            MarketNotice.OverlaySellLine(8210, "Terminal", "12m ago"),
            MarketNotice.DossierHeroLine(8210, "Terminal", "12m ago"),
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
