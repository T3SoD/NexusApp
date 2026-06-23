using System.Collections.Generic;

namespace NexusApp.Services;

/// <summary>
/// Pure resolution of which members a Blueprint Network view should cover. A non-null
/// <paramref name="personFilter"/> focuses a single member and excludes the local user; otherwise the
/// supplied group-or-all set is used and the local user is counted on top. No UI/storage deps.
/// </summary>
public static class NetworkScope
{
    public static NetworkScopeResult Resolve(string? personFilter, IReadOnlyList<string> groupOrAllMemberIds)
    {
        if (!string.IsNullOrEmpty(personFilter))
            return new NetworkScopeResult(new[] { personFilter }, includeSelf: false, focusPersonId: personFilter);

        return new NetworkScopeResult(groupOrAllMemberIds, includeSelf: true, focusPersonId: null);
    }
}

public sealed class NetworkScopeResult
{
    public NetworkScopeResult(IReadOnlyList<string> scopeIds, bool includeSelf, string? focusPersonId)
    {
        ScopeIds = scopeIds;
        IncludeSelf = includeSelf;
        FocusPersonId = focusPersonId;
    }

    public IReadOnlyList<string> ScopeIds { get; }
    public bool IncludeSelf { get; }
    public string? FocusPersonId { get; }
    public int CoverageDenominator => ScopeIds.Count + (IncludeSelf ? 1 : 0);
}
