using Xunit;

namespace NexusApp.Tests;

// Issue #34: the overlay CLEAR button called _vm.ScanHistory.Clear() directly, bypassing the VM's
// ClearHistoryCommand. Both RECENT surfaces render from FilteredScanHistory, which only the
// command rebuilds - so the cleared rows stayed on screen until a filter pill click forced the
// rebuild. The VM cannot be instantiated under test (App statics), so this pins the source:
// every clear path must route through the command.
public class OverlayClearHistoryTests
{
    [Fact]
    public void OverlayClear_RoutesThroughTheVmCommand()
    {
        var src = SourceFiles.ReadAppSource(@"Views\OverlayWindow.xaml.cs");
        Assert.DoesNotContain("ScanHistory.Clear()", src);
        Assert.Contains("ClearHistoryCommand", src);
    }
}
