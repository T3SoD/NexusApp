using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// Locks the owner gate's pure matching rules (the Admin tab and the catalog-patch export hang
// off them). Mirrors AccessGateTests. The *Active properties touch App state and are exercised
// only through GatePreviewTests' short-circuit paths.
public class OwnerGateTests
{
    [Fact] public void Owner_IsOwner() => Assert.True(OwnerGate.IsOwner("TurboV1RG1N"));
    [Fact] public void Owner_IsCaseInsensitive() => Assert.True(OwnerGate.IsOwner("turbov1rg1n"));
    [Fact] public void Owner_TrimsWhitespace() => Assert.True(OwnerGate.IsOwner("  TurboV1RG1N  "));
    [Fact] public void Unknown_IsNotOwner() => Assert.False(OwnerGate.IsOwner("SomeRando"));
    [Fact] public void BetaTester_IsNotOwner() => Assert.False(OwnerGate.IsOwner("Rorran198"));
    [Fact] public void PublicConst_MatchesTheGate() => Assert.True(OwnerGate.IsOwner(OwnerGate.OwnerHandle));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_IsNotOwner(string? handle) => Assert.False(OwnerGate.IsOwner(handle));
}
