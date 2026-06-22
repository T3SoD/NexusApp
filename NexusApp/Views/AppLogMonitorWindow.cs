using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NexusApp.Services;

namespace NexusApp.Views;

// Live viewer for Nexus's OWN log (%AppData%\NexusApp\logs\nexus.log) with a Save-snapshot button
// that bundles app/system context + the log so a user can send it for debugging. Mirrors the
// Game.log monitor in spirit but is read-only and Nexus-focused. Reuses GameLogWatcher purely as a
// generic shared-read file tailer (its blueprint categorization is irrelevant here).
public sealed class AppLogMonitorWindow : Window
{
    private const int MaxEntries = 8000;

    private readonly GameLogWatcher _watcher = new();
    private readonly List<string> _all = new();
    private readonly ListBox _list;
    private readonly TextBox _filterBox;
    private readonly CheckBox _errorsOnly;
    private readonly CheckBox _autoScroll;
    private readonly TextBlock _status;

    private static Brush Res(string key) => (Brush)System.Windows.Application.Current.FindResource(key);
    private static readonly FontFamily Mono = new("Consolas, Cascadia Mono, Lucida Console, monospace");

    public AppLogMonitorWindow()
    {
        Title = "App Log Monitor — Nexus";
        Width = 940; Height = 560; MinWidth = 600; MinHeight = 380;
        Background = Res("BgBrush");
        Foreground = Res("FgBrush");
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var root = new Grid { Margin = new Thickness(10) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // 0 controls
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });  // 1 list
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // 2 status

        _list = new ListBox
        {
            Background = Res("Bg2NavBrush"), BorderBrush = Res("NavBorderBrush"),
            BorderThickness = new Thickness(1), FontFamily = Mono, FontSize = 12, Margin = new Thickness(0, 8, 0, 0),
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Auto);

        // Row 0 — controls
        var ctl = new StackPanel { Orientation = Orientation.Horizontal };
        ctl.Children.Add(new TextBlock { Text = "Filter:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0), Foreground = Res("FgBrush") });
        _filterBox = new TextBox { Width = 220, Padding = new Thickness(6, 5, 6, 5), ToolTip = "Show only lines containing this text" };
        _filterBox.TextChanged += (_, _) => RebuildView();
        ctl.Children.Add(_filterBox);
        _errorsOnly = MakeCheck("Errors only", false);
        _errorsOnly.Checked += (_, _) => RebuildView();
        _errorsOnly.Unchecked += (_, _) => RebuildView();
        ctl.Children.Add(_errorsOnly);
        _autoScroll = MakeCheck("Auto-scroll", true);
        ctl.Children.Add(_autoScroll);
        var clearBtn = MakeButton("Clear view"); clearBtn.Margin = new Thickness(12, 0, 0, 0);
        clearBtn.Click += (_, _) => { _all.Clear(); _list.Items.Clear(); };
        ctl.Children.Add(clearBtn);
        var copyBtn = MakeButton("Copy snapshot"); copyBtn.Margin = new Thickness(6, 0, 0, 0);
        copyBtn.Click += (_, _) => CopySnapshot();
        ctl.Children.Add(copyBtn);
        var saveBtn = MakeButton("Save snapshot…"); saveBtn.Margin = new Thickness(6, 0, 0, 0);
        saveBtn.ToolTip = "Save app/system info + this log to a file you can send to T3SoD for debugging";
        saveBtn.Click += (_, _) => SaveSnapshot();
        ctl.Children.Add(saveBtn);
        Grid.SetRow(ctl, 0); root.Children.Add(ctl);

        Grid.SetRow(_list, 1); root.Children.Add(_list);

        _status = new TextBlock
        {
            Text = "Loading log…", Foreground = Res("FgDimBrush"),
            Margin = new Thickness(0, 8, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetRow(_status, 2); root.Children.Add(_status);

        Content = root;

        _watcher.LineAppended  += OnLine;
        _watcher.StatusChanged += s => _status.Text = s;
        _watcher.LogReset      += () => { _all.Clear(); _list.Items.Clear(); };   // 1 MB rotation truncates the log
        Closed += (_, _) => _watcher.Dispose();

        // Load the existing log from the top, then keep tailing — the bug has usually already happened.
        _watcher.Start(Logger.LogPath, fromBeginning: true);
    }

    private void OnLine(GameLogEntry e)
    {
        _all.Add(e.Raw);
        if (_all.Count > MaxEntries) _all.RemoveRange(0, _all.Count - MaxEntries);
        if (!Passes(e.Raw)) return;
        AddRow(e.Raw);
        while (_list.Items.Count > MaxEntries) _list.Items.RemoveAt(0);
        if (_autoScroll.IsChecked == true && _list.Items.Count > 0)
            _list.ScrollIntoView(_list.Items[_list.Items.Count - 1]);
    }

    private bool Passes(string line)
    {
        if (_errorsOnly.IsChecked == true && line.IndexOf("[ERROR]", StringComparison.OrdinalIgnoreCase) < 0) return false;
        var f = _filterBox.Text;
        return string.IsNullOrWhiteSpace(f) || line.Contains(f, StringComparison.OrdinalIgnoreCase);
    }

    private void AddRow(string line) => _list.Items.Add(new TextBlock
    {
        Text = line,
        Foreground = line.IndexOf("[ERROR]", StringComparison.OrdinalIgnoreCase) >= 0 ? Brushes.IndianRed : Res("FgBrush"),
        FontFamily = Mono, FontSize = 12, TextWrapping = TextWrapping.NoWrap,
    });

    private void RebuildView()
    {
        _list.Items.Clear();
        foreach (var l in _all) if (Passes(l)) AddRow(l);
        if (_autoScroll.IsChecked == true && _list.Items.Count > 0)
            _list.ScrollIntoView(_list.Items[_list.Items.Count - 1]);
    }

    private string BuildSnapshot()
    {
        string log;
        try { log = File.Exists(Logger.LogPath) ? File.ReadAllText(Logger.LogPath) : "(no log file)"; }
        catch (Exception ex) { log = $"(could not read log: {ex.Message})"; }

        var settings = new List<(string, string)>
        {
            ("Distribution", AppInfo.Distribution),
            ("Theme", App.Settings.Current.Theme),
            ("Scan region set", App.Settings.Current.ScanRegion != null ? "yes" : "no"),
            ("Game.log path", string.IsNullOrEmpty(App.Settings.Current.GameLogPath) ? "(default / auto-detect)" : App.Settings.Current.GameLogPath),
            ("Session Tracking", App.Settings.Current.GameLogTrackSession ? "on" : "off"),
            ("Auto-Track Blueprints", App.Settings.Current.GameLogAutoTrack ? "on" : "off"),
        };

        return DiagnosticSnapshot.Build(
            AppInfo.Version, GameData.Version, App.Data.MiningDataVersion,
            Environment.OSVersion.VersionString, settings, log, DateTime.Now);
    }

    private void CopySnapshot()
    {
        try
        {
            Clipboard.SetText(BuildSnapshot());
            _status.Text = "Snapshot copied to the clipboard — paste it to T3SoD on Discord.";
        }
        catch (Exception ex) { _status.Text = $"Copy failed: {ex.Message}"; }
    }

    private void SaveSnapshot()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Text (*.txt)|*.txt",
            FileName = $"nexus_snapshot_{DateTime.Now:yyyyMMdd_HHmmss}.txt",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            File.WriteAllText(dlg.FileName, BuildSnapshot());
            _status.Text = $"Saved snapshot to {dlg.FileName}";
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
