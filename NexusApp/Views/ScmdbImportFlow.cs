using System;
using System.IO;
using System.Text;
using System.Windows;
using NexusApp.Services;

namespace NexusApp.Views;

/// <summary>
/// "Import from SCMDB" flow (issue #3): reads a scmdb.net blueprint-tracking export (.json),
/// resolves each completed blueprint's name through the SAME official-name + localization/custom-
/// name resolution the Game.log importer uses (<see cref="GameLogSession.ResolveName"/>), and
/// shows a preview/confirm gate (<see cref="ScmdbImportResultDialog"/>) before marking anything
/// owned. FILE IMPORT ONLY - no network surface of any kind. ADD-ONLY: never un-marks a
/// blueprint. Mirrors <see cref="BlueprintImportFlow"/>'s shape (file -> parse -> resolve -> plan
/// -> preview/confirm -> apply -> refresh) as a small, separate flow so the Game.log import stays
/// untouched. AMENDMENT 2 (design spec): ownership now applies only on explicit confirm, matching
/// the Game.log import's own preview-gate pattern (an earlier ruling had this apply immediately
/// and summarize afterward; that was reversed).
/// </summary>
public static class ScmdbImportFlow
{
    /// <summary>Runs the whole flow synchronously (a single &lt;=5 MB JSON parse is fast enough that
    /// no background thread or busy state is needed). Cancelling the file picker, or cancelling/
    /// closing the preview/confirm dialog, applies nothing and logs nothing.</summary>
    public static void Run(Window owner)
    {
        var picker = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import from SCMDB",
            Filter = "SCMDB export (*.json)|*.json",
            CheckFileExists = true,
        };
        if (picker.ShowDialog(owner) != true) return;   // cancel: no log, no dialog

        string text;
        try
        {
            // Cheap stat-only guard before reading the whole file into memory - the parser's own
            // byte-count guard (Encoding.UTF8.GetByteCount) still runs on whatever text we hand it,
            // this just avoids paying for the read+load of something already known to be oversized.
            var info = new FileInfo(picker.FileName);
            if (info.Length > ScmdbExportParser.MaxInputBytes)
            {
                MessageBox.Show(owner,
                    "That file is larger than 5 MB, which is bigger than any real SCMDB export - Nexus won't read it.",
                    "Import from SCMDB", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Shared read access (FileShare.ReadWrite), same as the Game.log reads elsewhere in the
            // app, so a file another program still has open (e.g. mid-download) doesn't hard-fail.
            using var fs = new FileStream(picker.FileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs, Encoding.UTF8);
            text = sr.ReadToEnd();
        }
        catch (Exception ex)
        {
            // Friendly IO error path - a locked or unreadable file is a message, never a crash.
            // The path itself is never included (usernames), matching the logging rule below.
            MessageBox.Show(owner, $"Couldn't read that file: {ex.Message}", "Import from SCMDB",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var parsed = ScmdbExportParser.Parse(text);
        if (!parsed.Success)
        {
            MessageBox.Show(owner, parsed.Error, "Import from SCMDB", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Same official-name + localization/custom-name pipeline the Game.log importer uses (same
        // Importer instance and live-tail localization map) - not re-derived here.
        var plan = ScmdbImportPlan.Build(parsed.CompletedNames, App.Settings.Current.OwnedBlueprints,
            App.GameLog.ResolveName);

        // Preview/confirm gate, computed from the plan BEFORE any ownership write - mirrors
        // BlueprintImportFlow's ImportResultDialog. Cancel (or the zero-toImport "Close") returns
        // false/null here, same as ImportResultDialog's own cancel path, and skips apply + log below.
        var dlg = new ScmdbImportResultDialog(plan.ToImport, plan.AlreadyOwned.Count, plan.Unrecognized,
            parsed.SkippedNotCompleted, parsed.MalformedEntries, parsed.MissionCount, parsed.NewerVersion)
        { Owner = owner };
        if (dlg.ShowDialog() != true) return;   // cancelled: apply nothing, log nothing

        int imported = App.Settings.SetBlueprintsOwned(plan.ToImport);
        App.GameLog.NotifyBulkOwnershipChanged();   // same refresh path the Game.log import uses

        Logger.Info($"[UI] SCMDB import: {imported} imported, {plan.AlreadyOwned.Count} already owned, {plan.Unrecognized.Count} unrecognized");
    }
}
