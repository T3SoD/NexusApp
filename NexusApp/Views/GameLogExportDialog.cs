using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NexusApp.Services;

namespace NexusApp.Views;

// Collects the range and the scrub choice for a Game.log export (issue #48), then writes the file.
// Sibling of GridExportDialog and built the same way: a plain code-built modal on the app's brushes.
//
// TIME. Game.log writes UTC on every line. The pickers here take LOCAL time, because that is what
// the user's clock says and what they mean by "the crash happened at 2pm", and GameLogExport
// converts. The row says so, so a user comparing the exported header against the raw file is not
// surprised by the offset.
public sealed class GameLogExportDialog : Window
{
    private readonly DatePicker _fromDate = new();
    private readonly DatePicker _toDate = new();
    private readonly TextBox _fromTime = new() { Text = "00:00" };
    private readonly TextBox _toTime = new() { Text = "23:59" };
    private readonly CheckBox _scrub = new() { IsChecked = true };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11.5 };
    private readonly string _logPath;

    public GameLogExportDialog(string logPath)
    {
        _logPath = logPath;

        Title = "Export Game.log";
        Width = 480; Height = 430; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)Application.Current.FindResource("BgBrush");
        Foreground = (Brush)Application.Current.FindResource("FgBrush");
        ResizeMode = ResizeMode.NoResize;
        FontFamily = (FontFamily)Application.Current.FindResource("UiFont");

        var fg = (Brush)Application.Current.FindResource("FgBrush");
        var fgDim = (Brush)Application.Current.FindResource("FgDimBrush");
        var stack = new StackPanel { Margin = new Thickness(18) };

        void Lbl(string t) => stack.Children.Add(new TextBlock
        {
            Text = t, Foreground = fgDim, Margin = new Thickness(0, 12, 0, 4), FontSize = 12,
        });

        stack.Children.Add(new TextBlock
        {
            Text = "Save a slice of Star Citizen's Game.log to attach to a bug report. Past sessions "
                 + "are included: the game keeps only the current one in Game.log and files the rest "
                 + "under logbackups, and both are searched. Times are your own local clock; Game.log "
                 + "itself records UTC and the export header states the converted range.",
            Foreground = fgDim, FontSize = 12, TextWrapping = TextWrapping.Wrap,
        });

        // Default range: today. The overwhelmingly common case is "the thing that just went wrong".
        var today = DateTime.Now.Date;
        _fromDate.SelectedDate = today;
        _toDate.SelectedDate = today;

        Lbl("From (date and time)");
        stack.Children.Add(Row(_fromDate, _fromTime));
        Lbl("To (date and time)");
        stack.Children.Add(Row(_toDate, _toTime));

        _scrub.Content = "Remove personal details (recommended)";
        _scrub.Foreground = fg;
        _scrub.Margin = new Thickness(0, 16, 0, 0);
        stack.Children.Add(_scrub);
        stack.Children.Add(new TextBlock
        {
            Text = "Removes your RSI handle, account and player ids, IP addresses and your Windows "
                 + "user folder name. Turn it off only if the developer asks for the raw file.",
            Foreground = fgDim, FontSize = 11, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(22, 3, 0, 0),
        });

        _status.Foreground = fgDim;
        _status.Margin = new Thickness(0, 14, 0, 0);
        stack.Children.Add(_status);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };
        var cancel = new Button { Content = "Close", Width = 92, Margin = new Thickness(0, 0, 8, 0) };
        cancel.Click += (_, _) => Close();
        var export = new Button { Content = "Export…", Width = 108 };
        export.Click += (_, _) => Export();
        buttons.Children.Add(cancel);
        buttons.Children.Add(export);
        stack.Children.Add(buttons);

        Content = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private static UIElement Row(DatePicker date, TextBox time)
    {
        date.Width = 160;
        time.Width = 80;
        time.Margin = new Thickness(8, 0, 0, 0);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(date);
        row.Children.Add(time);
        return row;
    }

    // "14:30" or "14:30:05", local. Anything unparseable falls back to the caller's default rather
    // than blocking the export on a typo in a field most users will never touch.
    private static DateTime? Combine(DateTime? date, string time, TimeSpan fallback)
    {
        if (date is not { } d) return null;
        var span = TimeSpan.TryParse(time?.Trim(), out var parsed) ? parsed : fallback;
        return d.Date + span;
    }

    private void Export()
    {
        if (string.IsNullOrWhiteSpace(_logPath) || !File.Exists(_logPath))
        {
            _status.Text = "No Game.log found. Set its path on the Game tab first.";
            return;
        }

        var fromLocal = Combine(_fromDate.SelectedDate, _fromTime.Text, TimeSpan.Zero);
        var toLocal = Combine(_toDate.SelectedDate, _toTime.Text, new TimeSpan(23, 59, 59));
        if (fromLocal is { } f && toLocal is { } t && t < f)
        {
            _status.Text = "The end of the range is before its start.";
            return;
        }

        var scrub = _scrub.IsChecked == true;
        try
        {
            var fromUtc = fromLocal?.ToUniversalTime();
            var toUtc = toLocal?.ToUniversalTime();

            // EVERY session in range, not just the live one. Star Citizen keeps only the current
            // session in Game.log and moves finished ones into logbackups, so reading the single
            // active file returned one evening no matter how wide a range was asked for.
            var paths = GameLogSources.Discover(_logPath, fromUtc);
            if (paths.Count == 0)
            {
                _status.Text = "No Game.log sessions found for that range.";
                return;
            }

            var sources = new List<GameLogSource>(paths.Count);
            foreach (var path in paths)
            {
                try { sources.Add(GameLogSources.Read(path)); }
                catch (Exception ex) { Logger.Info($"[UI] Game.log source skipped ({ex.Message})"); }
            }

            var handle = scrub ? App.Settings?.Current?.DetectedRsiHandle : null;
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            var result = GameLogExport.Build(sources, fromUtc, toUtc,
                scrub, handle, home, AppInfo.Version, DateTime.UtcNow);

            if (result.LinesKept == 0)
            {
                _status.Text = $"Nothing in that range across {result.FilesScanned} session "
                             + $"file{(result.FilesScanned == 1 ? "" : "s")} ({result.LinesTotal} lines). "
                             + "Widen the dates, or check that this is the session you meant.";
                return;
            }

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "Text (*.txt)|*.txt",
                FileName = GameLogExport.SuggestedFileName(DateTime.Now, scrub),
                InitialDirectory = DownloadsFolder(),
            };
            if (dlg.ShowDialog() != true) return;

            File.WriteAllText(dlg.FileName, result.Text);
            _status.Text = $"Saved {result.LinesKept} lines from {result.FilesWithContent} of "
                         + $"{result.FilesScanned} session files to {dlg.FileName}. "
                         + "Attach it to your issue at github.com/T3SoD/NexusApp/issues.";
            Logger.Info($"[UI] Game.log exported: {result.LinesKept}/{result.LinesTotal} lines from "
                      + $"{result.FilesWithContent}/{result.FilesScanned} session files, "
                      + $"personal details {(scrub ? "removed" : "kept")}");
        }
        catch (Exception ex)
        {
            _status.Text = $"Export failed: {ex.Message}";
            Logger.Info($"[UI] Game.log export failed: {ex.Message}");
        }
    }

    // The issue asks for "export to downloads". There is no SpecialFolder for it, so this is the
    // documented shell path; an empty string just lets the dialog use its own last location.
    private static string DownloadsFolder()
    {
        try
        {
            var p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            return Directory.Exists(p) ? p : "";
        }
        catch { return ""; }
    }
}
