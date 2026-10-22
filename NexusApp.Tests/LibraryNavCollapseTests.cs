using System.Text.Json;
using NexusApp.Models;
using NexusApp.Views;
using Xunit;

namespace NexusApp.Tests;

public class LibraryNavCollapseTests
{
    [Fact]
    public void NavColumnWidth_Is300Or34()
    {
        Assert.Equal(300, MainWindow.NavColumnWidth(false));
        Assert.Equal(34, MainWindow.NavColumnWidth(true));
    }

    [Fact]
    public void RailLabel_UppercasesAndCounts()
    {
        Assert.Equal("RIFLE · 7", MainWindow.RailLabel("Rifle", 7));
        Assert.Equal("RESULTS · 1", MainWindow.RailLabel("Results", 1));
    }

    [Fact]
    public void LibraryNavCollapsed_RoundTripsThroughJson()
    {
        Assert.False(new AppSettings().LibraryNavCollapsed);
        var json = JsonSerializer.Serialize(new AppSettings { LibraryNavCollapsed = true });
        Assert.Contains("\"LibraryNavCollapsed\":true", json);
        Assert.True(JsonSerializer.Deserialize<AppSettings>(json)!.LibraryNavCollapsed);
        Assert.False(JsonSerializer.Deserialize<AppSettings>("{}")!.LibraryNavCollapsed);
    }

    [Fact]
    public void WrapCrumb_DetachesTheCrumbBeforeWrapping()
    {
        // UI elements are DispatcherObjects; build them on an STA thread like the UI does.
        Exception? failure = null;
        var t = new Thread(() =>
        {
            try
            {
                // Same shape as RenderBlueprintNav: the breadcrumb is already the host's content.
                var host = new System.Windows.Controls.ContentControl();
                var crumb = new System.Windows.Controls.Border();
                host.Content = crumb;
                System.Windows.Controls.Button? toggle = null;
                MainWindow.WrapCrumb(host, () => toggle = new System.Windows.Controls.Button());

                var grid = Assert.IsType<System.Windows.Controls.Grid>(host.Content);
                Assert.Contains(crumb, grid.Children.Cast<System.Windows.UIElement>());
                Assert.Contains(toggle!, grid.Children.Cast<System.Windows.UIElement>());

                // A second render in a row wraps again without throwing.
                MainWindow.WrapCrumb(host, () => new System.Windows.Controls.Button());
                Assert.IsType<System.Windows.Controls.Grid>(host.Content);
            }
            catch (Exception ex) { failure = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
