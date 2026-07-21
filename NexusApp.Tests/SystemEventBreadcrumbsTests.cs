using Microsoft.Win32;
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// [WIN] display/power/session breadcrumb vocabulary. Suspend/Resume are logged; StatusChange
// (battery/AC blips on laptops) must stay OUT of the log or it drowns the diagnostics.
public class SystemEventBreadcrumbsTests
{
    [Fact]
    public void SuspendAndResume_ProduceWinTaggedLines()
    {
        Assert.StartsWith("[WIN]", SystemEventBreadcrumbs.PowerLine(PowerModes.Suspend));
        Assert.StartsWith("[WIN]", SystemEventBreadcrumbs.PowerLine(PowerModes.Resume));
    }

    [Fact]
    public void PowerStatusChange_IsFilteredOut()
    {
        Assert.Null(SystemEventBreadcrumbs.PowerLine(PowerModes.StatusChange));
    }

    [Fact]
    public void SessionSwitch_ProducesWinTaggedLineNamingTheReason()
    {
        var line = SystemEventBreadcrumbs.SessionLine(SessionSwitchReason.SessionLock);
        Assert.StartsWith("[WIN]", line);
        Assert.Contains("SessionLock", line);
    }
}
