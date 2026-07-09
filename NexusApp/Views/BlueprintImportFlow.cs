using System;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using NexusApp.Services;

namespace NexusApp.Views;

/// <summary>
/// Shared "import owned blueprints from past Game.logs" flow: scans the current Game.log + its
/// logbackups for blueprint receipts, shows the preview/confirm dialog, and applies ownership.
/// Used by BOTH the advanced Game.log monitor and the Blueprint Library's Import button so the two
/// behave identically (same scan, same dialog, same far-back coverage line). BETA: reads a
/// game-authored file; see [[nexus-gamelog-ownership]].
/// </summary>
public static class BlueprintImportFlow
{
    /// <param name="Applied">true only when the user confirmed and ownership was written.</param>
    public sealed record Result(bool Applied, int Added, int Found, int FilesScanned, string Status);

    /// <summary>Scan <paramref name="path"/> (+ its logbackups), confirm via dialog, mark owned.
    /// <paramref name="status"/> receives progress/result text for the caller's own status surface.</summary>
    public static async Task<Result> RunAsync(Window owner, string path, Action<string>? status = null)
    {
        status?.Invoke("Scanning logs…");
        GameLogBlueprintImporter.HistoryScan scan;
        string? buildLine;
        try
        {
            scan = await Task.Run(() => App.GameLog.ScanHistory(path,
                n => owner.Dispatcher.Invoke(() => status?.Invoke($"Scanning… {n} file(s)"))));
            buildLine = await Task.Run(() => UnrecognizedBlueprintReport.TryReadBuildLine(path));
        }
        catch (Exception ex) { return new Result(false, 0, 0, 0, $"Scan failed: {ex.Message}"); }

        // How far back the scan could see, logged so a low count reads as "older logs were overwritten".
        var oldest = scan.EarliestUtc.HasValue
            ? scan.EarliestUtc.Value.ToString("d MMM yyyy HH:mm", CultureInfo.InvariantCulture) + " UTC"
            : "unknown";
        Logger.Info($"[GameLog] past-logs import scan: {scan.FilesScanned} file(s), oldest data {oldest}, " +
                    $"{scan.Matched.Count} matched ({scan.FallbackMatched} via name fallback), {scan.Unmatched.Count} unrecognized, " +
                    $"localization map {(scan.LocalizationEntries.HasValue ? $"{scan.LocalizationEntries.Value} entries" : "not loaded")}");

        if (scan.Matched.Count == 0 && scan.Unmatched.Count == 0)
            return new Result(false, 0, 0, scan.FilesScanned, scan.EarliestUtc.HasValue
                ? $"No blueprint receipts found ({scan.FilesScanned} file(s) scanned, oldest data {oldest})."
                : $"No blueprint receipts found ({scan.FilesScanned} file(s) scanned).");

        // Report (for the dialog's Copy / Export) of the names we couldn't map. No PII: versions +
        // build line + raw names only.
        // Origin is a pure function of settings (mirrors App.BuildLocalizationMap's precedence).
        var locOrigin = string.IsNullOrWhiteSpace(App.Settings.Current.GlobalIniPath)
            ? "derived from Game.log path" : "Settings override";
        var report = UnrecognizedBlueprintReport.Build(
            AppInfo.Version, App.Data.MiningDataVersion, buildLine,
            scan.FilesScanned, scan.Matched.Count, scan.UnmatchedLines, scan.StarStringsDetected, DateTime.Now,
            scan.LocalizationEntries, locOrigin);

        var dlg = new ImportResultDialog(scan.Matched, scan.Unmatched, scan.FilesScanned, scan.EarliestUtc, report) { Owner = owner };
        if (dlg.ShowDialog() != true)
            return new Result(false, 0, scan.Matched.Count, scan.FilesScanned,
                scan.Matched.Count == 0 ? "Nothing to import." : "Import cancelled.");

        int added = App.Settings.SetBlueprintsOwned(scan.Matched);
        App.GameLog.NotifyBulkOwnershipChanged();
        return new Result(true, added, scan.Matched.Count, scan.FilesScanned,
            $"Imported {added} newly marked owned ({scan.Matched.Count} found, {scan.Matched.Count - added} were already owned).");
    }
}
