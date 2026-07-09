using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NexusApp.Services;

namespace NexusApp.Views;

// BETA / EXPERIMENTAL - floating viewer that tails Star Citizen's Game.log live and shows
// the raw lines, with a retroactive "import from past logs" scan. The live tail, blueprint
// auto-mark and the per-session tally all live in the shared App.GameLog session, so this
// window and the overlay's STATS tab share one watcher and stay in sync. This stays the
// full/advanced tool (raw log, filter, snapshot, import); the overlay is the lightweight
// in-game surface. Reads a game-authored file - rework the "no game files" EAC wording
// before any release. See GameLogSession / GameLogWatcher / GameLogBlueprintImporter.
public sealed class LogMonitorWindow : Window
{
    private const int MaxEntries = 6000;

    private readonly List<GameLogEntry> _all = new();
    private readonly ListBox _list;
    private readonly TextBox _pathBox;
    private readonly TextBox _globalIniBox;
    private readonly TextBox _filterBox;
    private readonly TextBlock _status;
    private readonly Button _startBtn;
    private readonly Button _bpBtn;
    private readonly Button _importBtn;
    private readonly CheckBox _autoScroll;
    private readonly CheckBox _fromStart;
    private readonly CheckBox _autoMark;
    private readonly TextBlock _markCountLabel;
    private bool _blueprintsOnly;
    private bool _syncing;   // guards programmatic control updates from re-entering the session

    private static Brush Res(string key) => (Brush)System.Windows.Application.Current.FindResource(key);
    private static readonly FontFamily Mono = new("Consolas, Cascadia Mono, Lucida Console, monospace");

    public LogMonitorWindow()
    {
        Title = "Game.log Monitor - Advanced";
        Width = 940; Height = 560; MinWidth = 600; MinHeight = 380;
        Background = Res("BgBrush");
        Foreground = Res("FgBrush");
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var root = new Grid { Margin = new Thickness(10) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 0 path
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 1 viewer controls
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 2 blueprint import
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 3 list
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 4 status

        // Built up front so earlier control lambdas (e.g. Clear) can capture it safely.
        _list = new ListBox
        {
            Background = Res("Bg2NavBrush"), BorderBrush = Res("NavBorderBrush"),
            BorderThickness = new Thickness(1), FontFamily = Mono, FontSize = 12, Margin = new Thickness(0, 8, 0, 0),
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Auto);

        // Row 0 - log path + browse + start/stop
        var pathRow = new Grid();
        pathRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        pathRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        pathRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _pathBox = new TextBox
        {
            Text = !string.IsNullOrEmpty(App.GameLog.Path) ? App.GameLog.Path
                 : !string.IsNullOrEmpty(App.GameLog.PreferredPath) ? App.GameLog.PreferredPath
                 : GameLogSession.DefaultPath,
            VerticalContentAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(6, 5, 6, 5),
            ToolTip = "Path to Star Citizen's Game.log (LIVE / PTU / EPTU)",
        };
        Grid.SetColumn(_pathBox, 0); pathRow.Children.Add(_pathBox);
        var browse = MakeButton("Browse…"); browse.Click += (_, _) => Browse();
        Grid.SetColumn(browse, 1); pathRow.Children.Add(browse);
        _startBtn = MakeButton(App.GameLog.IsRunning ? "Stop" : "Start");
        _startBtn.Margin = new Thickness(6, 0, 0, 0); _startBtn.Click += (_, _) => ToggleStart();
        Grid.SetColumn(_startBtn, 2); pathRow.Children.Add(_startBtn);

        // Optional localization file (global.ini). When set (or auto-detected next to Game.log),
        // the import translates blueprint names renamed by community localization mods - any custom
        // format - back to library names. Read-only.
        var globalRow = new Grid();
        globalRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        globalRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _globalIniBox = new TextBox
        {
            Text = App.Settings.Current.GlobalIniPath,
            VerticalContentAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(6, 5, 6, 5),
            ToolTip = "Optional: Star Citizen's global.ini (…\\Data\\Localization\\english). Leave blank to auto-detect next to Game.log. Read-only - used to translate mod-renamed blueprint names.",
        };
        Grid.SetColumn(_globalIniBox, 0); globalRow.Children.Add(_globalIniBox);
        // Remember the path as soon as it's set - same as the Game.log path - not just on import.
        _globalIniBox.LostFocus += (_, _) => PersistGlobalIniPath();
        var browseGlobal = MakeButton("Browse…"); browseGlobal.Click += (_, _) => BrowseGlobalIni();
        Grid.SetColumn(browseGlobal, 1); globalRow.Children.Add(browseGlobal);

        var pathStack = new StackPanel();
        pathStack.Children.Add(pathRow);
        pathStack.Children.Add(new TextBlock
        {
            Text = "Localization file for mod-renamed blueprints (optional - auto-detected if blank):",
            Foreground = Res("FgDimBrush"), FontSize = 11, Margin = new Thickness(0, 6, 0, 2),
        });
        pathStack.Children.Add(globalRow);
        Grid.SetRow(pathStack, 0); root.Children.Add(pathStack);

        // Row 1 - viewer controls
        var ctl = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        ctl.Children.Add(new TextBlock { Text = "Filter:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0), Foreground = Res("FgBrush") });
        _filterBox = new TextBox { Width = 240, Padding = new Thickness(6, 5, 6, 5), ToolTip = "Show only lines containing this text" };
        _filterBox.TextChanged += (_, _) => RebuildView();
        ctl.Children.Add(_filterBox);
        _bpBtn = MakeButton("Blueprints only"); _bpBtn.Margin = new Thickness(8, 0, 0, 0);
        _bpBtn.Click += (_, _) => { _blueprintsOnly = !_blueprintsOnly; _bpBtn.Background = _blueprintsOnly ? Res("AccentBrush") : Res("Bg2NavBrush"); RebuildView(); };
        ctl.Children.Add(_bpBtn);
        _autoScroll = MakeCheck("Auto-scroll", true); ctl.Children.Add(_autoScroll);
        _fromStart = MakeCheck("From start of file", false);
        _fromStart.ToolTip = "On = read the whole current session from the top; Off = only new lines from now";
        ctl.Children.Add(_fromStart);
        var clearBtn = MakeButton("Clear"); clearBtn.Margin = new Thickness(12, 0, 0, 0);
        clearBtn.Click += (_, _) => { _all.Clear(); _list.Items.Clear(); };
        ctl.Children.Add(clearBtn);
        var saveBtn = MakeButton("Save snapshot…"); saveBtn.Margin = new Thickness(6, 0, 0, 0);
        saveBtn.ToolTip = "Save the currently shown lines to a .txt so you can share them";
        saveBtn.Click += (_, _) => SaveSnapshot();
        ctl.Children.Add(saveBtn);
        Grid.SetRow(ctl, 1); root.Children.Add(ctl);

        // Row 2 - blueprint auto-import (the feature)
        var bp = new Border
        {
            Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(8, 6, 8, 6),
            Background = Res("Bg2NavBrush"), BorderBrush = Res("NavBorderBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
        };
        var bpRow = new StackPanel { Orientation = Orientation.Horizontal };
        bpRow.Children.Add(new TextBlock { Text = "Session tracking:", VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold, Foreground = Res("AccentBrush"), Margin = new Thickness(0, 0, 12, 0) });
        _autoMark = MakeCheck("Auto-Track Blueprints (collects them to your library)", App.GameLog.AutoMark);
        _autoMark.Margin = new Thickness(0, 0, 0, 0);
        _autoMark.ToolTip = "When the log shows a 'Received Blueprint' event, collect it (mark it Owned in your library) automatically";
        _autoMark.Checked   += (_, _) => { if (!_syncing) App.GameLog.SetAutoMark(true); };
        _autoMark.Unchecked += (_, _) => { if (!_syncing) App.GameLog.SetAutoMark(false); };
        bpRow.Children.Add(_autoMark);
        _importBtn = MakeButton("Import owned from past logs…"); _importBtn.Margin = new Thickness(16, 0, 0, 0);
        _importBtn.ToolTip = "Scan this log + the logbackups folder for blueprints you've already received, and mark them owned";
        _importBtn.Click += async (_, _) => await ImportFromLogsAsync();
        bpRow.Children.Add(_importBtn);
        _markCountLabel = new TextBlock
        {
            Text = App.GameLog.Count > 0 ? $"Collected this session: {App.GameLog.Count}" : "",
            Foreground = Res("AccentBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0),
        };
        bpRow.Children.Add(_markCountLabel);
        // Reset session: clears this session's collected-blueprint tally. Relocated here from the overlay
        // HUB footer when the HUB was rebuilt to the MOBIGLAS mock. Raises SessionReset, which OnSessionReset
        // (here) and the overlay HUB both mirror.
        var resetBtn = MakeButton("Reset session"); resetBtn.Margin = new Thickness(16, 0, 0, 0);
        resetBtn.ToolTip = "Clear this session's collected-blueprint tally";
        resetBtn.Click += (s, _) => { InteractionLog.Click("Reset session", (DependencyObject)s!); App.GameLog.Reset(); };
        bpRow.Children.Add(resetBtn);
        bp.Child = bpRow;
        Grid.SetRow(bp, 2); root.Children.Add(bp);

        // Row 3 - live list (created above)
        Grid.SetRow(_list, 3); root.Children.Add(_list);

        // Row 4 - status
        _status = new TextBlock
        {
            Text = "Tails Game.log. Turn on Auto-Track Blueprints to collect them live, or import from past logs.",
            Foreground = Res("FgDimBrush"), Margin = new Thickness(0, 8, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetRow(_status, 4); root.Children.Add(_status);

        Content = root;

        // Bind to the shared session. Unsubscribe on close so the app-lifetime session
        // doesn't accumulate handlers each time this window is reopened.
        App.GameLog.LineAppended += OnLine;
        App.GameLog.StatusChanged += OnStatus;
        App.GameLog.Marked += OnMarked;
        App.GameLog.StateChanged += OnStateChanged;
        App.GameLog.SessionReset += OnSessionReset;
        Closed += (_, _) =>
        {
            PersistGlobalIniPath();   // catch a path typed but never imported
            App.GameLog.LineAppended -= OnLine;
            App.GameLog.StatusChanged -= OnStatus;
            App.GameLog.Marked -= OnMarked;
            App.GameLog.StateChanged -= OnStateChanged;
            App.GameLog.SessionReset -= OnSessionReset;
        };
    }

    // Raw live tail display (the auto-mark itself now happens in the shared session).
    private void OnLine(GameLogEntry e)
    {
        _all.Add(e);
        if (_all.Count > MaxEntries) _all.RemoveRange(0, _all.Count - MaxEntries);
        if (!PassesFilter(e)) return;
        AddRow(e);
        while (_list.Items.Count > MaxEntries) _list.Items.RemoveAt(0);
        if (_autoScroll.IsChecked == true && _list.Items.Count > 0)
            _list.ScrollIntoView(_list.Items[_list.Items.Count - 1]);
    }

    private void OnStatus(string s) => _status.Text = s;

    private void OnMarked(BlueprintMark m)
    {
        _markCountLabel.Text = $"Collected this session: {App.GameLog.Count}";
        _status.Text = $"Collected: {m.Name}";
    }

    // A new SC session (or a manual reset elsewhere) cleared the tally - clear the count label
    // to match its empty initial state (Count is 0 again).
    private void OnSessionReset() => _markCountLabel.Text = "";

    // Keep Start/Stop + Auto-mark in step when the overlay (or anything) changes them.
    private void OnStateChanged()
    {
        _syncing = true;
        _startBtn.Content = App.GameLog.IsRunning ? "Stop" : "Start";
        _autoMark.IsChecked = App.GameLog.AutoMark;
        _syncing = false;
    }

    private async Task ImportFromLogsAsync()
    {
        var path = _pathBox.Text.Trim();
        PersistGlobalIniPath();   // honor the optional localization override; blank = auto-detect
        _importBtn.IsEnabled = false;
        // Shared with the Blueprint Library's Import button so both surfaces behave identically.
        var result = await BlueprintImportFlow.RunAsync(this, path, s => _status.Text = s);
        _importBtn.IsEnabled = true;
        _status.Text = result.Status;
    }

    private bool PassesFilter(GameLogEntry e)
    {
        if (_blueprintsOnly && e.Category != LogCategory.Blueprint) return false;
        var f = _filterBox.Text;
        return string.IsNullOrWhiteSpace(f) || e.Raw.Contains(f, StringComparison.OrdinalIgnoreCase);
    }

    private void AddRow(GameLogEntry e) => _list.Items.Add(new TextBlock
    {
        Text = $"{e.ReceivedAt:HH:mm:ss}  {e.Raw}",
        Foreground = ColorFor(e.Category),
        FontFamily = Mono, FontSize = 12, TextWrapping = TextWrapping.NoWrap,
    });

    private void RebuildView()
    {
        _list.Items.Clear();
        foreach (var e in _all) if (PassesFilter(e)) AddRow(e);
        if (_autoScroll.IsChecked == true && _list.Items.Count > 0)
            _list.ScrollIntoView(_list.Items[_list.Items.Count - 1]);
    }

    private static Brush ColorFor(LogCategory c) => c switch
    {
        LogCategory.Blueprint => Brushes.MediumSpringGreen,
        LogCategory.Kill => Brushes.IndianRed,
        LogCategory.Location => Brushes.MediumTurquoise,
        LogCategory.Mission => Brushes.Gold,
        LogCategory.Economy => Brushes.LightGreen,
        LogCategory.Version => Brushes.Khaki,
        LogCategory.Quantum => Brushes.CornflowerBlue,
        LogCategory.Login or LogCategory.Connection => Brushes.Plum,
        LogCategory.Spawn or LogCategory.VehicleDestruction => Brushes.SandyBrown,
        _ => Brushes.Gainsboro,
    };

    private void ToggleStart()
    {
        // Button content resyncs via OnStateChanged (which also covers overlay-driven changes).
        if (App.GameLog.IsRunning) App.GameLog.Stop();
        else App.GameLog.Start(_pathBox.Text.Trim(), _fromStart.IsChecked == true);
    }

    private void Browse()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Game log (*.log)|*.log|All files (*.*)|*.*", FileName = "Game.log" };
        if (dlg.ShowDialog() == true) _pathBox.Text = dlg.FileName;
    }

    private void BrowseGlobalIni()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Localization (*.ini)|*.ini|All files (*.*)|*.*", FileName = "global.ini" };
        if (dlg.ShowDialog() == true) { _globalIniBox.Text = dlg.FileName; PersistGlobalIniPath(); }
    }

    // Persists the user's chosen global.ini path (blank = auto-detect) so it survives restarts,
    // mirroring how the Game.log path is remembered once set.
    private void PersistGlobalIniPath()
    {
        var path = _globalIniBox.Text.Trim();
        if (App.Settings.Current.GlobalIniPath == path) return;   // no-op if unchanged
        App.Settings.Current.GlobalIniPath = path;
        App.Settings.Save();
        App.GameLog.InvalidateLocalizationMap();   // the live tail must pick up the new path now
    }

    private void SaveSnapshot()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "Text (*.txt)|*.txt", FileName = $"nexus_gamelog_{DateTime.Now:yyyyMMdd_HHmmss}.txt" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var lines = _all.Where(PassesFilter).Select(e => $"{e.ReceivedAt:HH:mm:ss}\t{e.Category}\t{e.Raw}");
            File.WriteAllLines(dlg.FileName, lines);
            _status.Text = $"Saved {_list.Items.Count} shown line(s) to {dlg.FileName}";
        }
        catch (Exception ex) { _status.Text = $"Save failed: {ex.Message}"; }
    }

    private Button MakeButton(string text) => new()
    {
        Content = text, Padding = new Thickness(12, 6, 12, 6),
        Background = Res("Bg2NavBrush"), Foreground = Res("FgBrush"),
        BorderBrush = Res("NavBorderBrush"), BorderThickness = new Thickness(1),
        Cursor = System.Windows.Input.Cursors.Hand,
    };

    private CheckBox MakeCheck(string text, bool isChecked) => new()
    {
        Content = text, IsChecked = isChecked, Foreground = Res("FgBrush"),
        VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0),
    };
}
