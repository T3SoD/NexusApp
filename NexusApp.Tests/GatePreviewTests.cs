using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// Session-only impersonation for the Admin tab. The properties that must hold: a preview role
// changes what the two *Active gates REPORT (before ever touching App state, so this is testable
// headless), the preview-blind IsOwnerReal is what keeps the Admin tab reachable, and the state
// never survives outside Set (nothing is persisted anywhere).
public class GatePreviewTests : IDisposable
{
    // Never leak an active preview into other test classes.
    public void Dispose() => GatePreview.Set(GatePreview.Role.None);

    [Fact]
    public void Default_IsNoPreview()
    {
        GatePreview.Set(GatePreview.Role.None);
        Assert.False(GatePreview.IsActive);
        Assert.Equal(GatePreview.Role.None, GatePreview.Active);
    }

    [Fact]
    public void BetaTesterPreview_ReportsApprovedButNotOwner()
    {
        GatePreview.Set(GatePreview.Role.BetaTester);
        Assert.True(AccessGate.IsApprovedActive);
        Assert.False(OwnerGate.IsOwnerActive);
    }

    [Fact]
    public void VisitorPreview_ReportsNeither()
    {
        GatePreview.Set(GatePreview.Role.Visitor);
        Assert.False(AccessGate.IsApprovedActive);
        Assert.False(OwnerGate.IsOwnerActive);
    }

    [Fact]
    public void IsOwnerReal_IgnoresThePreview()
    {
        // Headless there is no App.Settings, so IsOwnerReal is false; the property under test
        // is that flipping the preview cannot CHANGE it (the lockout-proof invariant).
        GatePreview.Set(GatePreview.Role.None);
        var before = OwnerGate.IsOwnerReal;
        GatePreview.Set(GatePreview.Role.Visitor);
        Assert.Equal(before, OwnerGate.IsOwnerReal);
    }

    [Fact]
    public void Changed_FiresOncePerTransition_NotOnRepeat()
    {
        GatePreview.Set(GatePreview.Role.None);
        int fired = 0;
        Action handler = () => fired++;
        GatePreview.Changed += handler;
        try
        {
            GatePreview.Set(GatePreview.Role.Visitor);
            GatePreview.Set(GatePreview.Role.Visitor);
            Assert.Equal(1, fired);
        }
        finally { GatePreview.Changed -= handler; }
    }
}
