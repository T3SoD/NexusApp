using NexusApp.Services;

namespace NexusApp.ViewModels;

// How a scan is recorded in RECENT SCANS. FindByRs ranks pinned ores above unpinned ones, so the
// top-ranked match can be a pinned CLOSE match sitting above an unpinned EXACT one - the row must
// still read as an exact decode, named for the exact ore (issue #34, filter toggles).
public static class ScanClassification
{
    public static (string TopName, MatchKind Kind) Summarize(IReadOnlyList<RsMatch> matches)
    {
        if (matches.Count == 0) return ("No match", MatchKind.None);
        var exact = matches.FirstOrDefault(m => m.IsExact);
        if (exact != null) return (exact.Resource.Name, MatchKind.Exact);
        return (matches[0].Resource.Name, MatchKind.Close);
    }
}
