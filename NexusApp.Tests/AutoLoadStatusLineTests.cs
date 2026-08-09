using Xunit;

namespace NexusApp.Tests;

public class AutoLoadStatusLineTests
{
    private static string Src() => SourceFiles.ReadAppSource(@"Views\AutoLoadStatusLine.cs");

    [Fact]
    public void ClicksRouteThroughTracker_AndLogUi()
    {
        var src = Src();
        Assert.Contains("App.AutoLoad.Complete(", src);
        Assert.Contains("App.AutoLoad.Discard(", src);
        Assert.Contains("[UI] auto-load LOADED", src);
    }

    [Fact]
    public void TickerExpiresStale_AndNeverLogsPerTick()
    {
        var src = Src();
        Assert.Contains("App.AutoLoad.ExpireStale()", src);
        // Range binds tighter than '+' in C# (a..b + c parses as (a..b) + c, not a..(b + c)),
        // so the index needs its own local rather than the brief's inline expression.
        var tickIndex = src.IndexOf("void OnTick");
        var tickBody = src[tickIndex..(tickIndex + 400)];
        Assert.DoesNotContain("Logger.Info", tickBody);
    }

    [Fact]
    public void UsesTheCountdownFoldOnly()
    {
        var src = Src();
        Assert.Contains("AutoLoadStatusText.Clock(", src);
        Assert.DoesNotContain("AutoLoadStatusText.Elapsed(", src);
    }

    // The progress bar lives in BuildEntryRow (gated by !_compact at runtime, since that builder
    // is shared with the compact strip's expanded rows) and must never appear in BuildStrip, the
    // compact strip's own builder.
    [Fact]
    public void ProgressBar_InTheRowBuilder_NeverInTheStrip()
    {
        var src = Src();
        Assert.Contains("AutoLoadStatusText.Progress(", src);
        var stripStart = src.IndexOf("private Border BuildStrip");
        var stripEnd = src.IndexOf("private static StackPanel BuildEyebrow");
        Assert.True(stripStart > 0 && stripEnd > stripStart, "BuildStrip method markers not found");
        var stripBody = src[stripStart..stripEnd];
        Assert.DoesNotContain("AutoLoadStatusText.Progress(", stripBody);
    }

    [Fact]
    public void Overlay_HostsTheCompactStrip()
    {
        var xaml = SourceFiles.ReadAppSource(@"Views\OverlayWindow.xaml");
        Assert.Contains("AutoLoadStripHost", xaml);
        var cs = SourceFiles.ReadAppSource(@"Views\OverlayWindow.xaml.cs");
        Assert.Contains("new AutoLoadStatusLine(compact: true", cs);
    }

    [Fact]
    public void TradePage_HostsTheStandardPanel_AboveProfit()
    {
        var src = SourceFiles.ReadAppSource(@"Views\TradePage.cs");
        var line = src.IndexOf("new AutoLoadStatusLine(compact: false");
        Assert.True(line > 0, "Trade page must host the standard AutoLoadStatusLine");
        Assert.True(line < src.IndexOf("BuildProfitPanel()"), "auto-load panel mounts above the profit panel");
    }
}
