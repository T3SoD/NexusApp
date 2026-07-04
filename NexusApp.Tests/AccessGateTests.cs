using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

public class AccessGateTests
{
    [Fact] public void Owner_IsApproved() => Assert.True(AccessGate.IsApproved("TurboV1RG1N"));
    [Fact] public void Approved_IsCaseInsensitive() => Assert.True(AccessGate.IsApproved("turbov1rg1n"));
    [Fact] public void Approved_TrimsWhitespace() => Assert.True(AccessGate.IsApproved("  TurboV1RG1N  "));
    [Fact] public void Unknown_IsNotApproved() => Assert.False(AccessGate.IsApproved("SomeRando"));

    [Fact]
    public void EmptyOrNull_IsNotApproved()
    {
        Assert.False(AccessGate.IsApproved(""));
        Assert.False(AccessGate.IsApproved(null));
    }
}
