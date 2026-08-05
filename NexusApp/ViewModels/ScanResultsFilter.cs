namespace NexusApp.ViewModels;

// The RECENT SCANS pills also govern the live result cards (issue #34). The matcher only emits
// exact or <=0.5% close matches, so All and Exact+Close both keep everything; Exact narrows to
// exact decodes only.
public static class ScanResultsFilter
{
    public static IEnumerable<MatchResult> Apply(IEnumerable<MatchResult> results, HistoryFilter filter)
        => filter == HistoryFilter.Exact ? results.Where(r => r.IsExact) : results;
}
