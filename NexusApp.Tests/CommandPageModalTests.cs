using Xunit;

namespace NexusApp.Tests;

// Source pins for the Operations page's modal layer. The system view's starmap is a native
// WebView2 window, so no WPF modal can draw over it; every modal on the page must show and
// hide through the helper pair that parks the map while the modal is up (the install-update
// prompt rendered behind the starmap before this rule, 2026-08-20).
public class CommandPageModalTests
{
    [Fact]
    public void OnlyTheHelperPair_TouchesTheModalHostVisibility()
    {
        var src = SourceFiles.ReadAppSource(@"Views\CommandPage.cs");
        // Exactly one show site (inside ShowModalHost) and one hide site (inside HideModalHost).
        // A second occurrence of either means a modal bypassed the map-parking helpers.
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(
            src, @"_modalHost\.Visibility = Visibility\.Visible").Count);
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(
            src, @"_modalHost\.Visibility = Visibility\.Collapsed").Count);
        Assert.Contains("HideMapForModal();", src);
        Assert.Contains("RestoreMapAfterModal();", src);
    }

    [Fact]
    public void ParkingTheMap_UsesHidden_SoTheLayoutHoleStays()
    {
        var src = SourceFiles.ReadAppSource(@"Views\CommandPage.SystemView.cs");
        Assert.Contains("_mapView.Visibility = Visibility.Hidden", src);
        Assert.Contains("_mapView.Visibility = Visibility.Visible", src);
    }
}
