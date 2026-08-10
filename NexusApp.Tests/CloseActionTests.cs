using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// Issue #46: the X button can exit, minimize, or hide to the notification area.
public class CloseActionTests
{
    [Theory]
    [InlineData("exit", CloseBehaviour.Exit)]
    [InlineData("minimize", CloseBehaviour.Minimize)]
    [InlineData("tray", CloseBehaviour.Tray)]
    public void Parse_ReadsEachStoredValue(string stored, CloseBehaviour expected)
        => Assert.Equal(expected, CloseAction.Parse(stored));

    // A hand-edited settings.json, or one written by a later version that grew a fourth option,
    // must not throw or trap the user in a window they cannot close. Exit is both the safe answer
    // and what the app did before this setting existed.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("TRAY")]        // case matters: the stored form is lowercase
    [InlineData("hibernate")]
    public void Parse_FallsBackToExitOnAnythingUnrecognised(string? stored)
        => Assert.Equal(CloseBehaviour.Exit, CloseAction.Parse(stored));

    [Theory]
    [InlineData(CloseBehaviour.Exit)]
    [InlineData(CloseBehaviour.Minimize)]
    [InlineData(CloseBehaviour.Tray)]
    public void ToStoredAndBack_RoundTrips(CloseBehaviour b)
        => Assert.Equal(b, CloseAction.Parse(CloseAction.ToStored(b)));

    [Fact]
    public void DefaultSettingIsExit_SoAnUpgradeChangesNothing()
        => Assert.Equal(CloseBehaviour.Exit,
            CloseAction.Parse(new NexusApp.Models.AppSettings().CloseButtonAction));

    // ---- source pins --------------------------------------------------------------------------

    // THE load-bearing decision. WM_CLOSE arrives only from the user (X button, system menu,
    // Alt+F4). WPF's Window.Close() and Application.Current.Shutdown() raise Closing DIRECTLY
    // without posting WM_CLOSE, so intercepting here leaves every programmatic exit already in the
    // app untouched: the update installer, the portable self-swap, the demo-profile restart and the
    // theme restart all still work. Cancelling in Closing instead would have caught all four and
    // silently broken updating.
    [Fact]
    public void MainWindow_InterceptsWmClose_NotTheClosingEvent()
    {
        var src = SourceFiles.ReadAppSource(@"Views\MainWindow.xaml.cs");
        Assert.Contains("WM_CLOSE = 0x0010", src);
        Assert.Contains("if (msg != WM_CLOSE) return IntPtr.Zero;", src);
    }

    // Hiding the window must not touch the overlay: hiding the desktop window is exactly what a
    // player does mid-session, and killing their in-game HUD at that moment is the opposite of what
    // they asked for.
    [Fact]
    public void HideToTray_LeavesTheOverlayAlone()
    {
        var src = SourceFiles.ReadAppSource(@"Views\MainWindow.xaml.cs");
        var start = src.IndexOf("private void HideToTray()", StringComparison.Ordinal);
        Assert.True(start > 0, "HideToTray must exist");
        var body = src[start..(start + 700)];
        Assert.DoesNotContain("_overlay", body);
    }

    // The tray icon is raw Shell_NotifyIcon rather than WinForms' NotifyIcon: this app publishes
    // self-contained win-x64, and UseWindowsForms would drag the WinForms stack into every portable
    // download for one icon, undoing (several times over) the 6 MB the csproj already trims.
    [Fact]
    public void TrayIcon_UsesWin32_NotWindowsForms()
    {
        Assert.Contains("Shell_NotifyIcon", SourceFiles.ReadAppSource(@"Services\TrayIcon.cs"));
        Assert.DoesNotContain("UseWindowsForms", SourceFiles.ReadAppSource(@"NexusApp.csproj"));
    }

    // An app that vanishes with no explanation reads as a crash, and the user goes looking in Task
    // Manager rather than the notification area. Shown once ever, hence the persisted flag.
    [Fact]
    public void FirstHideToTray_ExplainsWhereTheWindowWent()
    {
        var src = SourceFiles.ReadAppSource(@"Views\MainWindow.xaml.cs");
        Assert.Contains("TrayHintShown", src);
        Assert.Contains("ShowHint", src);
    }
}
