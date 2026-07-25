using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

public class AccessGateTests
{
    [Fact] public void Owner_IsApproved() => Assert.True(AccessGate.IsApproved("TurboV1RG1N"));
    [Fact] public void Approved_IsCaseInsensitive() => Assert.True(AccessGate.IsApproved("turbov1rg1n"));
    [Fact] public void Approved_TrimsWhitespace() => Assert.True(AccessGate.IsApproved("  TurboV1RG1N  "));
    [Fact] public void Unknown_IsNotApproved() => Assert.False(AccessGate.IsApproved("SomeRando"));
    [Fact] public void BetaTester_Rorran198_IsApproved() => Assert.True(AccessGate.IsApproved("Rorran198"));

    [Fact]
    public void EmptyOrNull_IsNotApproved()
    {
        Assert.False(AccessGate.IsApproved(""));
        Assert.False(AccessGate.IsApproved(null));
    }

    [Fact] public void Testers_ContainsKnownTester() => Assert.Contains("Rorran198", AccessGate.Testers);
    [Fact] public void Testers_ExcludesTheOwner() => Assert.DoesNotContain(OwnerGate.OwnerHandle, AccessGate.Testers);
    [Fact] public void Testers_AreSortedCaseInsensitively()
        => Assert.Equal(AccessGate.Testers.OrderBy(h => h, StringComparer.OrdinalIgnoreCase), AccessGate.Testers);
    [Fact] public void EveryRosteredTester_IsApproved()
        => Assert.All(AccessGate.Testers, h => Assert.True(AccessGate.IsApproved(h)));
}
