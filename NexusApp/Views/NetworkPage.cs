using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NexusApp.Models;
using NexusApp.Services;

namespace NexusApp.Views;

/// <summary>
/// The Blueprint Network page (code-built, like the other Nexus dialogs). Slice 4 = the shell:
/// header with Import/Export, a sub-tab bar (Overview / Blueprints / Members), a group switcher,
/// and the functional Members list. Overview and Blueprints are placeholders until later slices.
/// </summary>
public sealed class NetworkPage : UserControl
{
    private readonly NetworkStore _store;
    private readonly SettingsService _settings;
    private readonly NetworkFileService _files = new();

    private string _tab = "members";     // overview | blueprints | members
    private string? _groupFilter;        // null = All, else a group id

    private readonly StackPanel _subTabBar = new() { Orientation = Orientation.Horizontal };
    private readonly WrapPanel _groupBar = new();
    private readonly Border _host = new();   // hosts the active sub-tab content

    private static Brush Br(string key) => (Brush)Application.Current.FindResource(key);
    private static FontFamily Head => (FontFamily)Application.Current.FindResource("HeadFont");

    public NetworkPage(NetworkStore store, SettingsService settings)
    {
        _store = store;
        _settings = settings;
        _files.AppLabel = $"NexusApp {AppInfo.Version}";
        Build();
    }

    /// <summary>Re-render groups + the active sub-tab (called when the page is shown and after imports).</summary>
    public void Refresh()
    {
        RenderGroups();
        RenderContent();
    }

    // ── layout ────────────────────────────────────────────────────────────────

    private void Build()
    {
        var root = new Grid { Margin = new Thickness(20, 16, 20, 16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // header
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // sub-tabs
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // groups
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });  // content

        // Header: title + Import/Export
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titleStack = new StackPanel();
        titleStack.Children.Add(new TextBlock { Text = "BLUEPRINT NETWORK", FontFamily = Head, FontSize = 21, Foreground = Br("FgBrush") });
        titleStack.Children.Add(new TextBlock { Text = "Shared blueprint libraries", FontSize = 12, Foreground = Br("FgDimBrush"), Margin = new Thickness(0, 2, 0, 0) });
        header.Children.Add(titleStack);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        var importBtn = ActionButton("Import"); importBtn.Click += (_, _) => Import();
        var exportBtn = ActionButton("Export"); exportBtn.Click += (_, _) => Export();
        actions.Children.Add(importBtn);
        actions.Children.Add(exportBtn);
        Grid.SetColumn(actions, 1);
        header.Children.Add(actions);
        Grid.SetRow(header, 0); root.Children.Add(header);

        _subTabBar.Margin = new Thickness(0, 14, 0, 12);
        Grid.SetRow(_subTabBar, 1); root.Children.Add(_subTabBar);
        RebuildSubTabs();

        _groupBar.Margin = new Thickness(0, 0, 0, 12);
        Grid.SetRow(_groupBar, 2); root.Children.Add(_groupBar);

        Grid.SetRow(_host, 3); root.Children.Add(_host);

        Content = root;
        RenderGroups();
        RenderContent();
    }

    private void RebuildSubTabs()
    {
        _subTabBar.Children.Clear();
        _subTabBar.Children.Add(SubTab("Overview", "overview"));
        _subTabBar.Children.Add(SubTab("Blueprints", "blueprints"));
        _subTabBar.Children.Add(SubTab("Members", "members"));
    }

    private UIElement SubTab(string label, string key)
    {
        var active = _tab == key;
        var tb = new TextBlock
        {
            Text = label, FontFamily = Head, FontSize = 13, FontWeight = FontWeights.Bold,
            Foreground = active ? Br("AccentBrush") : Br("FgDimBrush"),
        };
        var border = new Border
        {
            Padding = new Thickness(14, 9, 14, 9), Cursor = Cursors.Hand, Child = tb,
            BorderBrush = active ? Br("AccentBrush") : Brushes.Transparent,
            BorderThickness = new Thickness(0, 0, 0, 2),
        };
        border.MouseLeftButtonUp += (_, _) =>
        {
            if (_tab == key) return;
            _tab = key;
            InteractionLog.Nav($"Blueprint Network: {label}");
            RebuildSubTabs();
            RenderContent();
        };
        return border;
    }

    // ── group switcher ──────────────────────────────────────────────────────────

    private void RenderGroups()
    {
        _groupBar.Children.Clear();
        _groupBar.Children.Add(GroupChip($"All · {_store.MemberCount}", null));
        foreach (var g in _store.GetGroups())
            _groupBar.Children.Add(GroupChip($"{g.Name} · {_store.GetGroupMemberIds(g.Id).Count}", g.Id));
        _groupBar.Children.Add(GroupChip("+ New group", "__new__", isNew: true));
    }

    private UIElement GroupChip(string label, string? groupId, bool isNew = false)
    {
        var active = !isNew && _groupFilter == groupId;
        var tb = new TextBlock
        {
            Text = label, FontFamily = Head, FontSize = 11, FontWeight = FontWeights.Bold,
            Foreground = active ? Br("BgBrush") : (isNew ? Br("AccentBrush") : Br("FgDimBrush")),
        };
        var border = new Border
        {
            Padding = new Thickness(11, 6, 11, 6), Margin = new Thickness(0, 0, 7, 7), CornerRadius = new CornerRadius(13),
            Background = active ? Br("AccentBrush") : Br("Bg2NavBrush"),
            BorderBrush = Br("NavBorderBrush"), BorderThickness = new Thickness(1), Cursor = Cursors.Hand, Child = tb,
        };
        border.MouseLeftButtonUp += (_, _) =>
        {
            if (isNew) { CreateGroupPrompt(); return; }
            _groupFilter = groupId;
            InteractionLog.Nav("Blueprint Network: group filter");
            RenderGroups();
            RenderContent();
        };
        return border;
    }

    private void CreateGroupPrompt()
    {
        var name = PromptText("New group", "Group name (e.g. Friends, Vanguard Industries):", "");
        if (string.IsNullOrWhiteSpace(name)) return;
        _store.CreateGroup(name.Trim());
        Logger.Info("[NET] group created");
        RenderGroups();
    }

    // ── content ────────────────────────────────────────────────────────────────

    private void RenderContent()
    {
        _host.Child = _tab switch
        {
            "members"    => BuildMembersView(),
            "overview"   => Placeholder("Overview — group coverage and gaps land in the next update."),
            "blueprints" => Placeholder("Blueprints — per-blueprint ownership lands in the next update."),
            _            => Placeholder(""),
        };
    }

    private UIElement BuildMembersView()
    {
        var members = _store.GetMembers();
        if (_groupFilter != null)
        {
            var ids = new HashSet<string>(_store.GetGroupMemberIds(_groupFilter));
            members = members.Where(m => ids.Contains(m.Id)).ToList();
        }
        if (members.Count == 0)
            return Placeholder("No one shared yet. Import a teammate's .nexuslib file, or Export yours to share.");

        var groupNames = _store.GetGroups().ToDictionary(g => g.Id, g => g.Name);
        var list = new StackPanel();
        foreach (var m in members) list.Children.Add(MemberRow(m, groupNames));
        return new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = list };
    }

    private UIElement MemberRow(NetworkMember m, Dictionary<string, string> groupNames)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var info = new StackPanel { Margin = new Thickness(12, 9, 12, 9) };
        info.Children.Add(new TextBlock { Text = m.DisplayName, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Br("FgBrush") });

        var kind = m.IdentityKind == NetworkIdentityKind.Handle ? "RSI handle" : "nickname";
        var groupsText = string.Join(", ", _store.GetMemberGroupIds(m.Id)
            .Where(groupNames.ContainsKey).Select(id => groupNames[id]));
        var sub = $"{kind} · updated {m.LastUpdatedUtc.ToLocalTime():g}";
        if (!string.IsNullOrEmpty(groupsText)) sub += $" · {groupsText}";
        info.Children.Add(new TextBlock { Text = sub, FontSize = 10.5, Foreground = Br("FgDimBrush"), Margin = new Thickness(0, 2, 0, 0) });
        Grid.SetColumn(info, 0); grid.Children.Add(info);

        var remove = ActionButton("Remove");
        remove.VerticalAlignment = VerticalAlignment.Center;
        remove.Margin = new Thickness(0, 0, 10, 0);
        remove.Click += (_, _) => RemoveMember(m);
        Grid.SetColumn(remove, 1); grid.Children.Add(remove);

        return new Border
        {
            Background = Br("Bg2NavBrush"), BorderBrush = Br("NavBorderBrush"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 0, 6), Child = grid,
        };
    }

    private void RemoveMember(NetworkMember m)
    {
        if (MessageBox.Show($"Remove {m.DisplayName} from your network? Their shared library will be deleted from this app.",
            "Remove member", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _store.DeleteMember(m.Id);
        Logger.Info($"[NET] member removed (count now {_store.MemberCount})");
        Refresh();
    }

    // ── import ────────────────────────────────────────────────────────────────

    private void Import()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Nexus library (*.nexuslib)|*.nexuslib|All files (*.*)|*.*",
            Title = "Import a shared library",
        };
        if (dlg.ShowDialog() != true) return;

        NetworkFile file;
        try { file = _files.Load(dlg.FileName); }
        catch (NetworkFileException ex)
        {
            Logger.Info($"[NET] import error: {ex.Message}");
            MessageBox.Show(ex.Message, "Couldn't import", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var selfId = string.IsNullOrEmpty(_settings.Current.LocalNetworkId) ? null : _settings.Current.LocalNetworkId;

        var collisions = _files.DetectCollisions(file, _store, selfId);
        if (collisions.Count > 0)
        {
            var names = string.Join(", ", collisions.Select(c => c.Name).Distinct());
            var r = MessageBox.Show(
                $"These look like people already in your network: {names}.\n\nMerge the incoming data into them? Choose No to add them as separate entries.",
                "Possible duplicates", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (r == MessageBoxResult.Cancel) return;
            if (r == MessageBoxResult.Yes) MergeIds(file, collisions);
        }

        var known = new HashSet<string>(App.Data.GetAllBlueprints().Select(b => b.Name), StringComparer.OrdinalIgnoreCase);
        var opts = new ImportOptions
        {
            SelfId = selfId,
            SelfHandle = string.IsNullOrEmpty(_settings.Current.DetectedRsiHandle) ? null : _settings.Current.DetectedRsiHandle,
            KnownBlueprints = known,
        };
        var res = _files.Import(file, _store, opts);
        Logger.Info($"[NET] import done: +{res.NewMembers} new, {res.UpdatedMembers} updated, {res.BlueprintsMatched} matched, {res.BlueprintsUnrecognized} unrecognized");

        var msg = $"Added {res.NewMembers} and updated {res.UpdatedMembers} member(s).";
        if (res.SkippedSelf > 0) msg += "\nSkipped your own entry.";
        if (!string.IsNullOrEmpty(res.GroupName)) msg += $"\nFiled under \"{res.GroupName}\".";
        if (res.BlueprintsUnrecognized > 0) msg += $"\n{res.BlueprintsUnrecognized} blueprint name(s) weren't recognised (kept as-is).";
        MessageBox.Show(msg, "Import complete", MessageBoxButton.OK, MessageBoxImage.Information);
        Refresh();
    }

    // Rewrite incoming ids to the existing members' ids so Import matches by GUID = a merge.
    private static void MergeIds(NetworkFile file, IReadOnlyList<NameCollision> collisions)
    {
        var map = collisions.ToDictionary(c => c.IncomingId, c => c.ExistingId);
        void Fix(NetworkFileMember? m) { if (m != null && map.TryGetValue(m.Id, out var existing)) m.Id = existing; }
        Fix(file.Member);
        if (file.Members != null) foreach (var m in file.Members) Fix(m);
    }

    // ── export ────────────────────────────────────────────────────────────────

    private void Export()
    {
        App.GameLog?.DetectHandleFromCurrentFile();   // on-demand: pull the handle now if we can
        var detected = _settings.Current.DetectedRsiHandle ?? "";

        var choice = AskExportChoice(detected);
        if (choice == null) return;

        var selfId = _settings.EnsureLocalNetworkId();
        var kind = choice.UseHandle ? NetworkIdentityKind.Handle : NetworkIdentityKind.Nickname;
        _settings.SetLocalIdentity(choice.DisplayName, kind);

        var now = DateTime.UtcNow;
        var selfHandle = choice.UseHandle ? detected : null;
        NetworkFile file;
        if (choice.Roster)
        {
            var members = new List<NetworkFileMember>
            {
                _files.BuildSelfLibrary(selfId, choice.DisplayName, kind, selfHandle, _settings.Current.OwnedBlueprints, now).Member!,
            };
            foreach (var m in _store.GetMembers())
                members.Add(NetworkFileService.ToFileMember(m, _store.GetOwnedNames(m.Id)));
            var groupName = _groupFilter != null ? _store.GetGroups().FirstOrDefault(g => g.Id == _groupFilter)?.Name : null;
            file = _files.BuildRoster(groupName, members, now);
        }
        else
        {
            file = _files.BuildSelfLibrary(selfId, choice.DisplayName, kind, selfHandle, _settings.Current.OwnedBlueprints, now);
        }

        var save = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Nexus library (*.nexuslib)|*.nexuslib",
            FileName = (choice.Roster ? "roster" : SafeFileName(choice.DisplayName)) + ".nexuslib",
        };
        if (save.ShowDialog() != true) return;
        try
        {
            _files.Save(save.FileName, file);
            var count = choice.Roster ? (file.Members?.Count ?? 0) : 1;
            Logger.Info($"[NET] export: kind={file.Kind}, identity={kind}, members={count}");
            MessageBox.Show($"Exported to {save.FileName}.\nShare it with your group — they Import it to see your blueprints.",
                "Export complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Logger.Info($"[NET] export error: {ex.Message}");
            MessageBox.Show($"Couldn't write the file: {ex.Message}", "Export failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private sealed class ExportChoice
    {
        public bool UseHandle;
        public string DisplayName = "";
        public bool Roster;
    }

    private ExportChoice? AskExportChoice(string detectedHandle)
    {
        var hasHandle = !string.IsNullOrEmpty(detectedHandle);
        var win = new Window
        {
            Title = "Export library", Width = 400, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = Window.GetWindow(this),
            Background = Br("BgBrush"), Foreground = Br("FgBrush"), ResizeMode = ResizeMode.NoResize,
        };
        var panel = new StackPanel { Margin = new Thickness(16) };

        panel.Children.Add(SectionLabel("Share as:"));
        var handleRadio = new RadioButton
        {
            GroupName = "id", Foreground = Br("FgBrush"), Margin = new Thickness(0, 6, 0, 0), IsEnabled = hasHandle, IsChecked = hasHandle,
            Content = hasHandle ? $"RSI handle: {detectedHandle}" : "RSI handle (not detected — launch SC with Nexus open)",
        };
        var nickRadio = new RadioButton { GroupName = "id", Foreground = Br("FgBrush"), Margin = new Thickness(0, 6, 0, 0), IsChecked = !hasHandle, Content = "Nickname:" };
        var nickBox = new TextBox { Margin = new Thickness(20, 4, 0, 0), Padding = new Thickness(6, 4, 6, 4) };
        panel.Children.Add(handleRadio);
        panel.Children.Add(nickRadio);
        panel.Children.Add(nickBox);

        panel.Children.Add(SectionLabel("Include:"));
        var meRadio = new RadioButton { GroupName = "scope", Foreground = Br("FgBrush"), Margin = new Thickness(0, 6, 0, 0), IsChecked = true, Content = "Just me" };
        var allRadio = new RadioButton { GroupName = "scope", Foreground = Br("FgBrush"), Margin = new Thickness(0, 4, 0, 0), Content = $"Everyone ({_store.MemberCount} + me) as a roster" };
        panel.Children.Add(meRadio);
        panel.Children.Add(allRadio);

        var ok = ActionButton("Export");
        var cancel = ActionButton("Cancel");
        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        btnRow.Children.Add(cancel);
        btnRow.Children.Add(ok);
        panel.Children.Add(btnRow);

        win.Content = panel;
        ExportChoice? result = null;
        ok.Click += (_, _) =>
        {
            var useHandle = handleRadio.IsChecked == true;
            var name = useHandle ? detectedHandle : nickBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                MessageBox.Show("Enter a nickname, or pick your RSI handle.", "Export", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            result = new ExportChoice { UseHandle = useHandle, DisplayName = name, Roster = allRadio.IsChecked == true };
            win.DialogResult = true;
        };
        cancel.Click += (_, _) => win.DialogResult = false;
        return win.ShowDialog() == true ? result : null;
    }

    // ── helpers ────────────────────────────────────────────────────────────────

    private string? PromptText(string title, string prompt, string initial)
    {
        var win = new Window
        {
            Title = title, Width = 360, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = Window.GetWindow(this),
            Background = Br("BgBrush"), Foreground = Br("FgBrush"), ResizeMode = ResizeMode.NoResize,
        };
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = prompt, Foreground = Br("FgBrush"), TextWrapping = TextWrapping.Wrap });
        var box = new TextBox { Text = initial, Margin = new Thickness(0, 10, 0, 0), Padding = new Thickness(6, 5, 6, 5) };
        panel.Children.Add(box);
        var ok = ActionButton("OK");
        var cancel = ActionButton("Cancel");
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        row.Children.Add(cancel);
        row.Children.Add(ok);
        panel.Children.Add(row);
        win.Content = panel;

        string? result = null;
        ok.Click += (_, _) => { result = box.Text; win.DialogResult = true; };
        cancel.Click += (_, _) => win.DialogResult = false;
        box.Loaded += (_, _) => box.Focus();
        return win.ShowDialog() == true ? result : null;
    }

    private static string SafeFileName(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return string.IsNullOrWhiteSpace(s) ? "library" : s;
    }

    private Button ActionButton(string text) => new()
    {
        Content = text, Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(6, 0, 0, 0),
        Background = Br("Bg2NavBrush"), Foreground = Br("AccentBrush"),
        BorderBrush = Br("NavBorderBrush"), BorderThickness = new Thickness(1),
        Cursor = Cursors.Hand, FontWeight = FontWeights.SemiBold, FontSize = 12,
    };

    private TextBlock SectionLabel(string text) => new()
    {
        Text = text, FontWeight = FontWeights.Bold, Foreground = Br("FgDimBrush"), Margin = new Thickness(0, 12, 0, 0),
    };

    private UIElement Placeholder(string text) => new TextBlock
    {
        Text = text, Foreground = Br("FgDimBrush"), FontSize = 13, TextWrapping = TextWrapping.Wrap,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(40),
    };
}
