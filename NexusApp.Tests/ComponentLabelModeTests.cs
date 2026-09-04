using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

public class ComponentLabelModeTests
{
    [Theory]
    [InlineData("off", ComponentLabelScope.Off)]
    [InlineData("library", ComponentLabelScope.Library)]
    [InlineData("everywhere", ComponentLabelScope.Everywhere)]
    public void Parse_ReadsEachStoredValue(string stored, ComponentLabelScope expected)
        => Assert.Equal(expected, ComponentLabelMode.Parse(stored));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("LIBRARY")]   // case matters: the stored form is lowercase
    [InlineData("all")]
    public void Parse_UnknownValue_FallsBackToOff(string? stored)
        => Assert.Equal(ComponentLabelScope.Off, ComponentLabelMode.Parse(stored));

    [Theory]
    [InlineData(ComponentLabelScope.Off)]
    [InlineData(ComponentLabelScope.Library)]
    [InlineData(ComponentLabelScope.Everywhere)]
    public void ToStored_RoundTripsThroughParse(ComponentLabelScope scope)
        => Assert.Equal(scope, ComponentLabelMode.Parse(ComponentLabelMode.ToStored(scope)));

    [Fact]
    public void Default_IsOff_SoUpgradesChangeNothing()
        => Assert.Equal(ComponentLabelScope.Off,
            ComponentLabelMode.Parse(new NexusApp.Models.AppSettings().ComponentLabels));

    [Theory]
    [InlineData(ComponentLabelScope.Off, "OFF")]
    [InlineData(ComponentLabelScope.Library, "LIBRARY")]
    [InlineData(ComponentLabelScope.Everywhere, "EVERYWHERE")]
    public void Label_NamesEachScope(ComponentLabelScope scope, string label)
        => Assert.Equal(label, ComponentLabelMode.Label(scope));

    // The render seam: what a surface actually asks. Library surfaces decorate at Library
    // and Everywhere; every other surface decorates only at Everywhere.
    [Theory]
    [InlineData(ComponentLabelScope.Off, false, false)]
    [InlineData(ComponentLabelScope.Library, true, false)]
    [InlineData(ComponentLabelScope.Everywhere, true, true)]
    public void DecoratesFlags_FollowTheScope(ComponentLabelScope scope, bool library, bool everywhere)
    {
        Assert.Equal(library, ComponentLabelMode.DecoratesLibrary(scope));
        Assert.Equal(everywhere, ComponentLabelMode.DecoratesEverywhere(scope));
    }

    // ---- source pins ----

    // The feature's load-bearing rule: decoration happens at RENDER only. If ComponentLabels
    // ever reaches the ingest/storage side, decorated strings land in ledger history, owned
    // flags, or export files, and survive the setting being turned off.
    [Fact]
    public void IngestAndStorage_NeverTouchComponentLabels()
    {
        foreach (var src in new[]
        {
            @"Services\ProfitTracker.cs",       // stamps ShopPurchase.DisplayName
            @"Services\GameLogSession.cs",      // resolves and stores BlueprintMark.Name
            @"Services\GameLogBlueprintImporter.cs",
            @"Views\WalletDisplay.cs",          // pure title helpers, called from render sites
            @"Services\UnmatchedBlueprintLog.cs",
        })
            Assert.DoesNotContain("ComponentLabels", SourceFiles.ReadAppSource(src));
    }

    // The wallet decorates at the render call site, gated on the purchase token.
    [Fact]
    public void WalletRow_DecoratesAtRender_TokenGated()
    {
        Assert.Contains("ComponentLabels.GlobalForToken(p.ItemToken",
            SourceFiles.ReadAppSource(@"Views\MoneyPanel.cs"));
    }

    // Live propagation: the pills write through the App single-write path, and the parked
    // Library repaints on the broadcast instead of waiting for its next navigation (the
    // staleness found in first-launch testing).
    [Fact]
    public void SettingWrites_GoThroughTheSingleWritePath()
    {
        Assert.Contains("App.SetComponentLabels(", SourceFiles.ReadAppSource(@"Views\SettingsPage.cs"));
        Assert.Contains("ComponentLabelsChanged?.Invoke", SourceFiles.ReadAppSource(@"App.xaml.cs"));
        Assert.Contains("App.ComponentLabelsChanged +=", SourceFiles.ReadAppSource(@"Views\MainWindow.Blueprints.cs"));
    }
}
