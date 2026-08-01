using NexusApp.Services;
using NexusApp.ViewModels;
using Xunit;

namespace NexusApp.Tests;

// StatusChips (F14 header consolidation): the pure decisions behind the four-lamp status strip.
// SessionValue pins what the merged GAME SESSION chip says once the SHARD chip folds into it;
// AutoScanCombined pins how two scanner states fold into one LED (the owner: the chip must represent
// BOTH OCR auto-scan features).
public class StatusChipsTests
{
    // ── ShardText: the old SHARD chip's format, verbatim ──

    [Fact]
    public void ShardText_RegionOnly()
        => Assert.Equal("US-E", StatusChips.ShardText("US-E", "", "LIVE"));

    [Fact]
    public void ShardText_RegionAndInstance()
        => Assert.Equal("US-E · 042", StatusChips.ShardText("US-E", "042", "LIVE"));

    [Fact]
    public void ShardText_NonLiveChannel_Appends()
        => Assert.Equal("US-E · 042 · PTU", StatusChips.ShardText("US-E", "042", "PTU"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void ShardText_NoRegion_IsNull(string? region)
        // Null means "render silence": the chip must never say "not detected" now that the shard
        // is a suffix on the session chip rather than its own surface.
        => Assert.Null(StatusChips.ShardText(region, "042", "LIVE"));

    // ── SessionValue: the merged chip's value. No "monitoring" (owner, 2026-08-01): the breathing
    // green dot and the SESSION label already say "alive", so live carries only facts. ──

    [Fact]
    public void SessionValue_LiveWithShard_IsJustTheShard()
        => Assert.Equal("US-E · 042", StatusChips.SessionValue(true, "", "US-E · 042"));

    [Fact]
    public void SessionValue_LiveNoShardNoChannel_IsEmpty()
        // Correct silence: nothing detected yet means nothing to say - the dot carries "alive".
        => Assert.Equal("", StatusChips.SessionValue(true, "", null));

    [Fact]
    public void SessionValue_LiveShardPlusChannelSuffix()
        // ChipSuffix's real format: empty on LIVE, " · CHANNEL" otherwise (GameChannels).
        => Assert.Equal("US-E · PTU", StatusChips.SessionValue(true, " · PTU", "US-E"));

    [Fact]
    public void SessionValue_LiveChannelOnly_DropsTheLeadingSeparator()
        => Assert.Equal("PTU", StatusChips.SessionValue(true, " · PTU", null));

    [Fact]
    public void SessionValue_Offline_NeverCarriesShard()
        // A shard reading with the session offline is history, not status.
        => Assert.Equal("offline", StatusChips.SessionValue(false, "", "US-E · 042"));

    // ── ContractScanState: extracted verbatim from the overlay's SyncHaulingControls ──

    [Theory]
    [InlineData(true,  true,  ScanIndicator.On)]
    [InlineData(true,  false, ScanIndicator.On)]      // running wins regardless of the toggle
    [InlineData(false, true,  ScanIndicator.Paused)]  // enabled but not running = foreground-gated
    [InlineData(false, false, ScanIndicator.Off)]
    public void ContractScanState_Mapping(bool running, bool enabled, ScanIndicator expected)
        => Assert.Equal(expected, StatusChips.ContractScanState(running, enabled));

    // ── AutoScanCombined: paused outranks on outranks off ──

    [Theory]
    [InlineData(ScanIndicator.On,     ScanIndicator.On,     ScanIndicator.On)]
    [InlineData(ScanIndicator.On,     ScanIndicator.Off,    ScanIndicator.On)]
    [InlineData(ScanIndicator.Off,    ScanIndicator.On,     ScanIndicator.On)]
    [InlineData(ScanIndicator.On,     ScanIndicator.Paused, ScanIndicator.Paused)]
    [InlineData(ScanIndicator.Paused, ScanIndicator.Off,    ScanIndicator.Paused)]
    [InlineData(ScanIndicator.Off,    ScanIndicator.Off,    ScanIndicator.Off)]
    public void AutoScanCombined_SeverityOrder(ScanIndicator rs, ScanIndicator ct, ScanIndicator expected)
        // Paused wins over On: the scanners pause themselves on foreground rules, and a green
        // lamp while either is silently paused would be the lamp lying.
        => Assert.Equal(expected, StatusChips.AutoScanCombined(rs, ct));

    [Fact]
    public void AutoScanText_SpellsOutBoth()
        => Assert.Equal("RS on · CT paused", StatusChips.AutoScanText(ScanIndicator.On, ScanIndicator.Paused));

    [Fact]
    public void AutoScanText_BothOff()
        => Assert.Equal("RS off · CT off", StatusChips.AutoScanText(ScanIndicator.Off, ScanIndicator.Off));
}
