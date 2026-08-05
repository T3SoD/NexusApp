using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// Issue #36: Windows Snap (drag-to-top / Win+Up) maximizes the borderless overlay; maximized,
// WPF's DragMove silently no-ops and the resize grip is template-hidden, so the window was stuck
// until the whole app closed. The guard converts the maximize into a normal-state fill of the
// monitor's WORK area: same coverage the user asked Snap for, still movable and resizable.
public class SnapMaximizeGuardTests
{
    private static readonly PxRect Monitor = new(2560, 0, 1920, 1080);
    private static readonly PxRect WorkArea = new(2560, 0, 1920, 1032);   // taskbar excluded

    [Fact]
    public void NormalMode_FillsTheWorkArea_NotTheFullMonitor()
    {
        var fill = SnapMaximizeGuard.FillRect(ghostActive: false, Monitor, WorkArea);
        Assert.Equal(WorkArea, fill);
    }

    [Fact]
    public void GhostMode_RevertsWithoutFilling()
    {
        // The ghost rail's footprint is mode-derived; a work-area fill would corrupt it.
        Assert.Null(SnapMaximizeGuard.FillRect(ghostActive: true, Monitor, WorkArea));
    }
}
