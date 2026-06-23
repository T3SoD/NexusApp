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

    private readonly Dictionary<string, Brush> _brushCache = new();
    private Brush Br(string key) => _brushCache.TryGetValue(key, out var b) ? b : (_brushCache[key] = (Brush)Application.Current.FindResource(key));
    private FontFamily? _head, _mono;
    private FontFamily Head => _head ??= (FontFamily)Application.Current.FindResource("HeadFont");
    private FontFamily Mono => _mono ??= (FontFamily)Application.Current.FindResource("MonoFont");

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
            "overview"   => BuildOverviewView(),
            "blueprints" => BuildBlueprintsView(),
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

        var btns = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        var groupsBtn = ActionButton("Groups…");
        groupsBtn.Click += (_, _) => EditMemberGroups(m);
        var remove = ActionButton("Remove");
        remove.Click += (_, _) => RemoveMember(m);
        btns.Children.Add(groupsBtn);
        btns.Children.Add(remove);
        Grid.SetColumn(btns, 1); grid.Children.Add(btns);

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

    private void EditMemberGroups(NetworkMember m)
    {
        var win = new Window
        {
            Title = $"Groups — {m.DisplayName}", Width = 340, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = Window.GetWindow(this),
            Background = Br("BgBrush"), Foreground = Br("FgBrush"), ResizeMode = ResizeMode.NoResize,
        };
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(SectionLabel("Member of:"));

        var inGroups = new HashSet<string>(_store.GetMemberGroupIds(m.Id));
        var checks = new List<(string id, CheckBox cb)>();
        foreach (var g in _store.GetGroups())
        {
            var cb = new CheckBox { Content = g.Name, IsChecked = inGroups.Contains(g.Id), Foreground = Br("FgBrush"), Margin = new Thickness(0, 6, 0, 0) };
            checks.Add((g.Id, cb));
            panel.Children.Add(cb);
        }
        if (checks.Count == 0)
            panel.Children.Add(new TextBlock { Text = "No groups yet — create one below.", Foreground = Br("FgDimBrush"), FontSize = 12, Margin = new Thickness(0, 6, 0, 0) });

        panel.Children.Add(SectionLabel("New group (optional):"));
        var newBox = new TextBox { Padding = new Thickness(6, 4, 6, 4) };
        panel.Children.Add(newBox);

        var ok = ActionButton("Save");
        var cancel = ActionButton("Cancel");
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        row.Children.Add(cancel);
        row.Children.Add(ok);
        panel.Children.Add(row);
        win.Content = panel;

        ok.Click += (_, _) =>
        {
            foreach (var (id, cb) in checks)
            {
                if (cb.IsChecked == true) _store.AddToGroup(id, m.Id);
                else _store.RemoveFromGroup(id, m.Id);
            }
            var newName = newBox.Text.Trim();
            if (!string.IsNullOrWhiteSpace(newName))
            {
                var g = _store.CreateGroup(newName);
                _store.AddToGroup(g.Id, m.Id);
            }
            Logger.Info("[NET] member group membership updated");
            win.DialogResult = true;
        };
        cancel.Click += (_, _) => win.DialogResult = false;
        if (win.ShowDialog() == true) Refresh();
    }

    // ── blueprints (coverage) ────────────────────────────────────────────────────

    private UIElement BuildBlueprintsView()
    {
        var catalog = App.Data.GetAllBlueprints();

        // Member scope: the selected group, or everyone. Self is always counted on top.
        var scopeIds = _groupFilter != null
            ? _store.GetGroupMemberIds(_groupFilter)
            : _store.GetMembers().Select(m => m.Id).ToList();
        var total = scopeIds.Count + 1;
        var counts = _store.OwnerCounts(scopeIds);

        int Owned(string name) =>
            (counts.TryGetValue(name, out var c) ? c : 0) + (_settings.IsBlueprintOwned(name) ? 1 : 0);

        // Surface blueprints owned by members (or you) that aren't in the local seed under an
        // "Unrecognized" group — they'd otherwise be invisible, since this list is built from the catalog.
        var catalogNames = new HashSet<string>(catalog.Select(b => b.Name), StringComparer.OrdinalIgnoreCase);
        var all = catalog.ToList();
        foreach (var n in UnrecognizedNames(counts.Keys, catalogNames))
            all.Add(new Blueprint { Name = n, Category = "Unrecognized" });

        var filters = new List<BlueprintListView.FilterChip>
        {
            new() { Label = "All",          Match = _ => true },
            new() { Label = "Nobody owns",  Match = b => Owned(b.Name) == 0 },
            new() { Label = "Single owner", Match = b => Owned(b.Name) == 1 },
            new() { Label = "I'm missing",  Match = b => !_settings.IsBlueprintOwned(b.Name) },
        };

        var listView = new BlueprintListView(
            all,
            b => CoverageCell(Owned(b.Name), total),
            b => OwnersPanel(b.Name, scopeIds),
            filters);

        // Make the "+ you" in every coverage denominator explicit (the group chip counts members only).
        var note = new TextBlock
        {
            Text = $"Coverage across you + {scopeIds.Count} member{(scopeIds.Count == 1 ? "" : "s")}"
                 + (_groupFilter != null ? " in this group." : "."),
            Foreground = Br("FgDimBrush"), FontSize = 11, Margin = new Thickness(2, 0, 0, 8),
        };
        var dock = new DockPanel();
        DockPanel.SetDock(note, Dock.Top);
        dock.Children.Add(note);
        dock.Children.Add(listView);
        return dock;
    }

    // Distinct blueprint names owned by members or by you that aren't in the local seed catalog.
    private List<string> UnrecognizedNames(IEnumerable<string> memberOwnedNames, HashSet<string> catalogNames)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in memberOwnedNames) if (!catalogNames.Contains(n)) set.Add(n);
        foreach (var n in _settings.Current.OwnedBlueprints) if (!catalogNames.Contains(n)) set.Add(n);
        return set.ToList();
    }

    private UIElement CoverageCell(int owned, int total)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        var pct = total > 0 ? (double)owned / total : 0;
        var fill = new Border
        {
            Height = 7, CornerRadius = new CornerRadius(4), HorizontalAlignment = HorizontalAlignment.Left,
            Width = Math.Max(0, 108 * pct),
            Background = owned == 0 ? Brushes.Transparent : (owned == 1 ? Amber() : Br("AccentBrush")),
        };
        var track = new Border
        {
            Width = 110, Height = 7, CornerRadius = new CornerRadius(4), Background = Br("BgBrush"),
            BorderBrush = Br("NavBorderBrush"), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 9, 0),
            VerticalAlignment = VerticalAlignment.Center, Child = fill,
        };
        row.Children.Add(track);

        row.Children.Add(new TextBlock
        {
            Text = $"{owned} / {total}", FontSize = 12, MinWidth = 54, TextAlignment = TextAlignment.Right,
            FontFamily = Mono,
            Foreground = owned == 0 ? Redish() : owned == 1 ? Amber() : Br("AccentBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        });
        return row;
    }

    private UIElement OwnersPanel(string bpName, IReadOnlyList<string> scopeIds)
    {
        var scope = new HashSet<string>(scopeIds);
        var ownerIds = _store.OwnerIdsOf(bpName).Where(scope.Contains).ToList();
        var nameMap = _store.GetMembers().ToDictionary(m => m.Id, m => m.DisplayName);

        var names = new List<string>();
        if (_settings.IsBlueprintOwned(bpName)) names.Add("You");
        names.AddRange(ownerIds.Select(id => nameMap.TryGetValue(id, out var nm) ? nm : "?"));

        var wrap = new WrapPanel();
        if (names.Count == 0)
        {
            wrap.Children.Add(new TextBlock { Text = "Nobody here owns it yet.", Foreground = Redish(), FontSize = 12 });
            return wrap;
        }
        wrap.Children.Add(new TextBlock
        {
            Text = $"Owned by {names.Count}:", Foreground = Br("FgDimBrush"), FontSize = 11,
            Margin = new Thickness(0, 0, 8, 6), VerticalAlignment = VerticalAlignment.Center,
        });
        foreach (var nm in names)
            wrap.Children.Add(new Border
            {
                Background = Br("BgBrush"), BorderBrush = Br("NavBorderBrush"), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(11), Padding = new Thickness(9, 3, 9, 3), Margin = new Thickness(0, 0, 6, 6),
                Child = new TextBlock { Text = nm, FontSize = 11.5, Foreground = Br("FgBrush") },
            });
        return wrap;
    }

    // Frozen + cached so the coverage list doesn't allocate a brush per row.
    private static readonly SolidColorBrush _amber = Frozen(0xE8, 0xA2, 0x3A);
    private static readonly SolidColorBrush _red = Frozen(0xE0, 0x6A, 0x55);
    private static SolidColorBrush Frozen(byte r, byte g, byte b) { var br = new SolidColorBrush(Color.FromRgb(r, g, b)); br.Freeze(); return br; }
    private static Brush Amber() => _amber;
    private static Brush Redish() => _red;

    // ── overview (dashboard) ─────────────────────────────────────────────────────

    private UIElement BuildOverviewView()
    {
        var all = App.Data.GetAllBlueprints();
        var scopeIds = _groupFilter != null
            ? _store.GetGroupMemberIds(_groupFilter)
            : _store.GetMembers().Select(m => m.Id).ToList();
        var scopeSet = new HashSet<string>(scopeIds);
        var counts = _store.OwnerCounts(scopeIds);
        int Owned(string n) => (counts.TryGetValue(n, out var c) ? c : 0) + (_settings.IsBlueprintOwned(n) ? 1 : 0);

        int total = all.Count, covered = 0, nobody = 0, single = 0;
        var singleBps = new List<string>();
        foreach (var b in all)
        {
            var o = Owned(b.Name);
            if (o == 0) nobody++;
            else
            {
                covered++;
                if (o == 1) { single++; if (singleBps.Count < 30) singleBps.Add(b.Name); }
            }
        }
        var pct = total > 0 ? (int)Math.Round(100.0 * covered / total) : 0;

        var body = new StackPanel();
        body.Children.Add(CoverageBand(pct, covered, total, nobody, single));

        // Per-member coverage cards (self + members in scope), most owned first.
        body.Children.Add(SectionHeader("Members"));
        var ownedCounts = _store.MemberOwnedCounts();
        var people = new List<(string name, int count, bool self)>
        {
            (string.IsNullOrEmpty(_settings.Current.LocalDisplayName) ? "You" : $"{_settings.Current.LocalDisplayName} (you)",
                _settings.OwnedBlueprintCount, true),
        };
        foreach (var m in _store.GetMembers())
        {
            if (_groupFilter != null && !scopeSet.Contains(m.Id)) continue;
            people.Add((m.DisplayName, ownedCounts.TryGetValue(m.Id, out var c) ? c : 0, false));
        }
        var cards = new WrapPanel();
        foreach (var person in people.OrderByDescending(p => p.count))
            cards.Children.Add(MemberCard(person.name, person.count, total, person.self));
        body.Children.Add(cards);

        // Watch list: gaps + single-owner risk.
        body.Children.Add(SectionHeader("Watch list"));
        body.Children.Add(WatchSummary(nobody, single));
        if (singleBps.Count > 0)
        {
            var nameMap = _store.GetMembers().ToDictionary(m => m.Id, m => m.DisplayName);
            string OwnerOf(string bp)
            {
                if (_settings.IsBlueprintOwned(bp)) return "you";
                var id = _store.OwnerIdsOf(bp).FirstOrDefault(scopeSet.Contains);
                return id != null && nameMap.TryGetValue(id, out var nm) ? nm : "?";
            }
            foreach (var bp in singleBps) body.Children.Add(SingleOwnerRow(bp, OwnerOf(bp)));
            if (single > singleBps.Count)
                body.Children.Add(new TextBlock
                {
                    Text = $"+{single - singleBps.Count} more single-owner blueprints", Foreground = Br("FgDimBrush"),
                    FontSize = 11.5, Margin = new Thickness(4, 6, 0, 0),
                });
        }

        var catalogNames = new HashSet<string>(all.Select(b => b.Name), StringComparer.OrdinalIgnoreCase);
        var unrecognized = UnrecognizedNames(counts.Keys, catalogNames);
        if (unrecognized.Count > 0)
            body.Children.Add(new TextBlock
            {
                Text = $"{unrecognized.Count} blueprint(s) members own aren't in your seed — see the Unrecognized group in the Blueprints tab.",
                Foreground = Br("FgDimBrush"), FontSize = 12, Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap,
            });

        return new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = body };
    }

    private UIElement CoverageBand(int pct, int covered, int total, int nobody, int single)
    {
        var grid = new Grid { Margin = new Thickness(14, 12, 16, 12) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(new TextBlock
        {
            Text = $"{pct}%", FontFamily = Head, FontSize = 40, FontWeight = FontWeights.Bold,
            Foreground = Br("AccentBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 20, 0),
        });
        var right = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(ProportionalBar(pct, Br("AccentBrush"), 10));
        right.Children.Add(new TextBlock
        {
            Text = $"{covered} of {total} covered  ·  {nobody} nobody owns  ·  {single} single-owner",
            FontSize = 11, Foreground = Br("FgDimBrush"), Margin = new Thickness(0, 7, 0, 0),
        });
        Grid.SetColumn(right, 1); grid.Children.Add(right);
        return new Border
        {
            Background = Br("Bg2NavBrush"), BorderBrush = Br("NavBorderBrush"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 0, 0, 14), Child = grid,
        };
    }

    private UIElement MemberCard(string name, int count, int total, bool self)
    {
        var inner = new StackPanel { Width = 118 };
        inner.Children.Add(new TextBlock { Text = name, FontSize = 12, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = self ? Br("AccentBrush") : Br("FgBrush") });
        inner.Children.Add(new TextBlock { Text = $"{count} owned", FontSize = 11, Foreground = Br("FgDimBrush"), Margin = new Thickness(0, 2, 0, 6) });
        var p = total > 0 ? (int)Math.Round(100.0 * count / total) : 0;
        inner.Children.Add(ProportionalBar(p, Br("AccentBrush"), 5));
        return new Border
        {
            Background = Br("BgBrush"), BorderBrush = Br("NavBorderBrush"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(11), Margin = new Thickness(0, 0, 10, 10), Child = inner,
        };
    }

    private UIElement WatchSummary(int nobody, int single)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        sp.Children.Add(new TextBlock
        {
            Text = $"{nobody} blueprints nobody owns — farm targets (filter Blueprints → Nobody owns to list them).",
            Foreground = nobody > 0 ? Redish() : Br("FgDimBrush"), FontSize = 12.5, TextWrapping = TextWrapping.Wrap,
        });
        if (single > 0)
            sp.Children.Add(new TextBlock
            {
                Text = $"{single} held by a single person — at risk if they stop sharing:",
                Foreground = Amber(), FontSize = 12.5, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap,
            });
        return sp;
    }

    private UIElement SingleOwnerRow(string bp, string owner)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = bp, Foreground = Br("FgBrush"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 6, 8, 6) });
        var who = new TextBlock { Text = $"only {owner}", Foreground = Amber(), FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        Grid.SetColumn(who, 1); grid.Children.Add(who);
        return new Border
        {
            Background = Br("Bg2NavBrush"), BorderBrush = Br("NavBorderBrush"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 0, 4), Child = grid,
        };
    }

    private UIElement ProportionalBar(int pct, Brush fill, double height)
    {
        pct = Math.Clamp(pct, 0, 100);
        var grid = new Grid { Height = height };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(pct, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100 - pct, GridUnitType.Star) });
        var f = new Border { Background = fill, CornerRadius = new CornerRadius(height / 2) };
        Grid.SetColumn(f, 0); grid.Children.Add(f);
        return new Border
        {
            Height = height, CornerRadius = new CornerRadius(height / 2), Background = Br("BgBrush"),
            BorderBrush = Br("NavBorderBrush"), BorderThickness = new Thickness(1), Child = grid,
        };
    }

    private UIElement SectionHeader(string text) => new TextBlock
    {
        Text = text.ToUpperInvariant(), FontFamily = Head, FontSize = 10, FontWeight = FontWeights.Bold,
        Foreground = Br("FgDimBrush"), Margin = new Thickness(2, 6, 0, 8),
    };

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
