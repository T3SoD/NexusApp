using System.Collections.Generic;
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

public class NetworkScopeTests
{
    private static readonly IReadOnlyList<string> GroupOrAll = new List<string> { "m1", "m2", "m3" };

    [Fact]
    public void PersonSelected_ScopesToThatPersonOnly_ExcludesSelf()
    {
        var r = NetworkScope.Resolve("m2", GroupOrAll);
        Assert.Equal(new[] { "m2" }, r.ScopeIds);
        Assert.False(r.IncludeSelf);
        Assert.Equal("m2", r.FocusPersonId);
        Assert.Equal(1, r.CoverageDenominator);
    }

    [Fact]
    public void NoPerson_UsesGivenMembers_IncludesSelf()
    {
        var r = NetworkScope.Resolve(null, GroupOrAll);
        Assert.Equal(GroupOrAll, r.ScopeIds);
        Assert.True(r.IncludeSelf);
        Assert.Null(r.FocusPersonId);
        Assert.Equal(4, r.CoverageDenominator); // 3 members + self
    }

    [Fact]
    public void PersonFilter_OverridesTheMemberList()
    {
        var r = NetworkScope.Resolve("m1", GroupOrAll);
        Assert.Equal(new[] { "m1" }, r.ScopeIds);   // the group/all list is ignored
        Assert.False(r.IncludeSelf);
        Assert.Equal("m1", r.FocusPersonId);
    }

    [Fact]
    public void EmptyPersonString_TreatedAsNoPerson()
    {
        var r = NetworkScope.Resolve("", GroupOrAll);
        Assert.Null(r.FocusPersonId);
        Assert.True(r.IncludeSelf);
        Assert.Equal(GroupOrAll, r.ScopeIds);
    }
}
