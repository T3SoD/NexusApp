using Xunit;

namespace NexusApp.Tests;

// The tracker must be constructed from ProfitTracker (single parse pipeline) right after the
// wallet wiring, and held as an app-wide static like the other trackers.
public class AutoLoadWiringTests
{
    [Fact]
    public void App_ConstructsAutoLoadFromProfit()
    {
        var src = SourceFiles.ReadAppSource(@"App.xaml.cs");
        Assert.Contains("public static AutoLoadTracker AutoLoad", src);
        Assert.Contains("AutoLoad = new AutoLoadTracker(Profit)", src);
    }
}
