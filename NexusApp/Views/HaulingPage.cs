using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using NexusApp.Models;
using NexusApp.Services;

namespace NexusApp.Views;

/// <summary>
/// The Cargo Hauling page (code-built, like NetworkPage), rebuilt onto the shared MOBIGLAS
/// Hud primitives. Reads App.Hauls and renders three sections: a two-up grid of active-haul
/// cards (per-leg load/drop rows), a load/drop consolidation TABLE, and a finished-hauls panel
/// with outcome chips. Rebuilds itself whenever the tracker raises Changed.
/// </summary>
public sealed class HaulingPage : UserControl
{
    private readonly StackPanel _body = new();
    private Button? _clearBtn;   // built once in the header; visibility toggled by Refresh()
    private Hud.ToggleSwitch? _autoScanToggle, _showBoxToggle;   // re-synced from shared contract state

    // Chip palette shared with the rest of the HUD (matches Hud.StateBar / Hud.StatusChip tints).
    private static readonly Color _amber = Color.FromRgb(0xFF, 0xB2, 0x3E);
    private static readonly Color _green = Color.FromRgb(0x66, 0xE6, 0xA6);
    private Color Cyan => Hud.Col("CyanBrush");

    private readonly Dictionary<string, Brush> _brushCache = new();
    private Brush Br(string key) => _brushCache.TryGetValue(key, out var b) ? b : (_brushCache[key] = (Brush)Application.Current.FindResource(key));
    private FontFamily? _head, _mono, _disp;
    private FontFamily Head => _head ??= (FontFamily)Application.Current.FindResource("HeadFont");
    private FontFamily Mono => _mono ??= (FontFamily)Application.Current.FindResource("MonoFont");
    private FontFamily Disp => _disp ??= (FontFamily)Application.Current.FindResource("DisplayFont");

    public HaulingPage()
    {
        Build();
        Refresh();
        InteractionLog.Nav("Cargo Hauling");
        App.Hauls.Changed += () => Dispatcher.Invoke(Refresh);
        // Keep the header toggles in lockstep with the overlay / shared state (the contract scanner and
        // the contract box can be flipped from the overlay or by foreground-gating). SetOnSilently
        // updates the visual without re-firing OnToggled, so re-syncing never re-starts/stops anything.
        App.ContractScan.RunningChanged += () => Dispatcher.Invoke(() => _autoScanToggle?.SetOnSilently(App.Settings.Current.AutoScanContracts));
        App.ContractBoxVisibilityChanged += on => Dispatcher.Invoke(() => _showBoxToggle?.SetOnSilently(on));
    }

    /// <summary>Rebuild every section from the current App.Hauls state.</summary>
    public void Refresh()
    {
        _body.Children.Clear();

        // Clear-all lives in the header (built once); only show it when there's something to clear.
        if (_clearBtn is not null)
            _clearBtn.Visibility = App.Hauls.AllHauls.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        if (App.Hauls.AllHauls.Count == 0)
        {
            _body.Children.Add(Placeholder("No active hauls. Accept a hauling contract in-game."));
            return;
        }

        RenderActive();
        RenderBottom();
    }

    // -- layout --------------------------------------------------------------------

    private void Build()
    {
        var root = new Grid { Margin = new Thickness(20, 16, 20, 16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // header
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });  // content

        // Header action slot: the two contract toggles (mirroring the overlay's HAULING tab) + Clear all.
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        // Auto-scan contracts drives the app-global ContractScanner and persists the choice, exactly
        // like the overlay's "Auto-scan contracts" switch (App.ContractScan / Settings.AutoScanContracts).
        var autoScan = new Hud.ToggleSwitch(App.Settings.Current.AutoScanContracts);   // reflects intent (stays on while foreground-paused)
        autoScan.OnToggled = on =>
        {
            if (on && !App.ContractScan.IsRunning) App.ContractScan.Start();
            else if (!on && App.ContractScan.IsRunning) App.ContractScan.Stop();
            App.Settings.Current.AutoScanContracts = App.ContractScan.IsRunning;
            App.Settings.Save();
            InteractionLog.Toggle($"Auto-scan contracts {(on ? "on" : "off")}", this);
        };
        _autoScanToggle = autoScan;
        actions.Children.Add(LabeledToggle("Auto-scan contracts", autoScan));

        // Show contract box reveals the yellow contract-detection indicator. Routes through the single
        // source (App.SetContractBoxVisible) so it actually shows/hides the box AND syncs the overlay.
        var showBox = new Hud.ToggleSwitch(App.ContractBoxVisible);
        showBox.OnToggled = on =>
        {
            App.SetContractBoxVisible(on);
            InteractionLog.Toggle($"Show contract box {(on ? "on" : "off")}", this);
        };
        _showBoxToggle = showBox;
        actions.Children.Add(LabeledToggle("Show contract box", showBox));

        _clearBtn = ActionButton("Clear all");
        _clearBtn.VerticalAlignment = VerticalAlignment.Center;
        _clearBtn.Margin = new Thickness(16, 0, 0, 0);
        _clearBtn.Click += (_, _) => App.Hauls.ClearAll();   // Changed -> Refresh() rebuilds the list
        actions.Children.Add(_clearBtn);

        var header = Hud.Header("LOGISTICS", "Cargo Hauling",
            "Tracking active contracts from Game.log and the contract OCR box.", actions);
        Grid.SetRow(header, 0); root.Children.Add(header);

        var scroller = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _body,
        };
        Grid.SetRow(scroller, 1); root.Children.Add(scroller);

        Content = root;
    }

    // A Hud toggle paired with its caption (switch on the left, label on the right, like the mock).
    private FrameworkElement LabeledToggle(string label, Hud.ToggleSwitch sw)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };
        sw.VerticalAlignment = VerticalAlignment.Center;
        p.Children.Add(sw);
        p.Children.Add(new TextBlock
        {
            Text = label, FontFamily = Head, FontSize = 11.5, FontWeight = FontWeights.SemiBold,
            Foreground = Br("FgDimBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0),
        });
        return p;
    }

    // -- active hauls --------------------------------------------------------------

    private void RenderActive()
    {
        var active = App.Hauls.ActiveHauls;
        _body.Children.Add(SectionHeader($"Active hauls · {active.Count}"));
        if (active.Count == 0)
        {
            _body.Children.Add(MutedLine("No active hauls right now."));
            return;
        }

        // Two-up card grid (the mock's grid2). Per-card margins create the gutters.
        var grid = new UniformGrid { Columns = 2 };
        foreach (var h in active)
            grid.Children.Add(ActiveHaulCard(h));
        _body.Children.Add(grid);
    }

    private UIElement ActiveHaulCard(Haul h)
    {
        var inner = new StackPanel();

        // Title row: company name + topology chip (left), aUEC reward, delete (right).
        var titleRow = new Grid();
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var nameStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        nameStack.Children.Add(new TextBlock
        {
            Text = Contractor(h), FontFamily = Head, FontSize = 14.5, FontWeight = FontWeights.SemiBold,
            Foreground = Br("FgBrush"), VerticalAlignment = VerticalAlignment.Center,
        });
        if (!string.IsNullOrWhiteSpace(h.Topology))
        {
            var topo = Hud.Chip(_amber, h.Topology);
            topo.Margin = new Thickness(10, 0, 0, 0);
            topo.VerticalAlignment = VerticalAlignment.Center;
            nameStack.Children.Add(topo);
        }
        Grid.SetColumn(nameStack, 0); titleRow.Children.Add(nameStack);

        if (h.Reward > 0)
        {
            var reward = new TextBlock
            {
                Text = $"{h.Reward:N0} aUEC", FontFamily = Mono, FontSize = 12, FontWeight = FontWeights.SemiBold,
                Foreground = Br("CyanBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0),
            };
            Grid.SetColumn(reward, 1); titleRow.Children.Add(reward);
        }
        var del = DeleteButton(h.MissionId);
        Grid.SetColumn(del, 2); titleRow.Children.Add(del);
        inner.Children.Add(titleRow);

        // Route line in mono cyan (the mock's commodity route).
        if (!string.IsNullOrWhiteSpace(h.RouteTitle))
            inner.Children.Add(new TextBlock
            {
                Text = h.RouteTitle, FontFamily = Mono, FontSize = 12, Foreground = Br("CyanBrush"),
                Margin = new Thickness(0, 6, 0, 8), TextWrapping = TextWrapping.Wrap,
            });
        else
            inner.Children.Add(new Border { Height = 6 });

        if (h.ContractObjectives.Count > 0)
            foreach (var o in h.ContractObjectives)
                inner.Children.Add(OcrObjectiveRow(o));
        else
            foreach (var leg in h.Legs)
                inner.Children.Add(LegRow(leg));

        var card = Hud.Panel(inner, brackets: true, padding: new Thickness(14, 12, 14, 12));
        card.Margin = new Thickness(0, 0, 10, 10);
        return card;
    }

    private UIElement LegRow(HaulLeg leg)
    {
        var role = leg.Role == HaulRole.Pickup ? "Load" : "Drop";

        // Pickup legs often carry no commodity/SCU/destination of their own (those live on the
        // sibling dropoff). Show whatever fields are present, skipping the empties.
        var segs = new List<string>();
        if (leg.TargetScu > 0) segs.Add($"{leg.TargetScu} SCU");
        if (!string.IsNullOrWhiteSpace(leg.Commodity)) segs.Add(leg.Commodity);
        if (!string.IsNullOrWhiteSpace(leg.Destination)) segs.Add($"-> {leg.Destination}");
        var desc = string.Join(" ", segs);

        var text = $"{role}: {desc}";

        // Progress row: a filled teal dot = leg completed, a hollow dot = still pending.
        var grid = new Grid { Margin = new Thickness(2, 6, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var dot = StatusDot(leg.Completed);
        Grid.SetColumn(dot, 0); grid.Children.Add(dot);
        var tb = new TextBlock
        {
            Text = text, FontFamily = Mono, FontSize = 12,
            Foreground = leg.Completed ? Br("FgDimBrush") : Br("FgBrush"),
            TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(tb, 1); grid.Children.Add(tb);
        return grid;
    }

    // Builds the display string for one OCR-sourced ContractObjective, omitting empty segments.
    private static string OcrObjectiveText(ContractObjective o)
    {
        var sb = new System.Text.StringBuilder();
        if (o.Scu > 0) sb.Append($"{o.Scu} SCU");
        if (!string.IsNullOrWhiteSpace(o.Commodity))
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(o.Commodity);
        }
        if (!string.IsNullOrWhiteSpace(o.Pickup)) sb.Append($": {o.Pickup}");
        if (!string.IsNullOrWhiteSpace(o.Dropoff)) sb.Append($" -> {o.Dropoff}");
        return sb.ToString();
    }

    private UIElement OcrObjectiveRow(ContractObjective o)
    {
        // OCR objectives are contract targets (no completion state), so the marker is a static teal pip.
        var grid = new Grid { Margin = new Thickness(2, 6, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var pip = new Border
        {
            Width = 7, Height = 7, CornerRadius = new CornerRadius(4),
            Background = Br("AccentFaintBrush"), BorderBrush = Br("AccentBrush"), BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 9, 0),
        };
        Grid.SetColumn(pip, 0); grid.Children.Add(pip);
        var tb = new TextBlock
        {
            Text = OcrObjectiveText(o), FontFamily = Mono, FontSize = 12, Foreground = Br("FgBrush"),
            TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(tb, 1); grid.Children.Add(tb);
        return grid;
    }

    // -- bottom row: consolidation table (left) + finished hauls (right) -----------

    private void RenderBottom()
    {
        var consolidation = BuildConsolidationPanel();
        var finished = App.Hauls.FinishedHauls;

        if (finished.Count == 0)
        {
            consolidation.Margin = new Thickness(0, 16, 0, 0);
            _body.Children.Add(consolidation);
            return;
        }

        var row = new Grid { Margin = new Thickness(0, 16, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        consolidation.Margin = new Thickness(0, 0, 8, 0);
        Grid.SetColumn(consolidation, 0); row.Children.Add(consolidation);

        var finishedPanel = BuildFinishedPanel();
        finishedPanel.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(finishedPanel, 1); row.Children.Add(finishedPanel);

        _body.Children.Add(row);
    }

    // -- consolidation -------------------------------------------------------------

    private Grid BuildConsolidationPanel()
    {
        var con = App.Hauls.BuildConsolidation();

        var bodyStack = new StackPanel();
        bodyStack.Children.Add(PanelHeaderBar("Load / drop consolidation", "grouped by location"));

        if (con.Pickups.Count == 0 && con.Dropoffs.Count == 0)
        {
            bodyStack.Children.Add(new Border { Padding = new Thickness(14, 10, 14, 14), Child = MutedLine("Nothing to consolidate yet.") });
            return Hud.Panel(bodyStack, padding: new Thickness(0));
        }

        var table = new Grid { Margin = new Thickness(14, 10, 14, 12) };
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.4, GridUnitType.Star) }); // Location
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                         // Action
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });    // Commodity
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                         // SCU

        table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AddCell(table, 0, 0, HeaderCell("LOCATION", false));
        AddCell(table, 0, 1, HeaderCell("ACTION", false));
        AddCell(table, 0, 2, HeaderCell("COMMODITY", false));
        AddCell(table, 0, 3, HeaderCell("SCU", true));

        var rowIdx = 1;
        foreach (var s in con.Pickups)
            foreach (var item in s.Items)
                rowIdx = AddConsolidationRow(table, rowIdx, s.Location, true, item.Commodity, item.Scu);
        foreach (var s in con.Dropoffs)
            foreach (var item in s.Items)
                rowIdx = AddConsolidationRow(table, rowIdx, s.Location, false, item.Commodity, item.Scu);

        bodyStack.Children.Add(table);
        return Hud.Panel(bodyStack, padding: new Thickness(0));
    }

    private int AddConsolidationRow(Grid table, int row, string location, bool load, string commodity, int scu)
    {
        table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var loc = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(location) ? "Unknown" : location, FontFamily = Head, FontSize = 12.5,
            Foreground = Br("FgBrush"), Margin = new Thickness(0, 5, 8, 5), VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        AddCell(table, row, 0, loc);

        // Colored action pill: Load = cyan, Drop = amber (per the mock's load/drop chips).
        var chip = Hud.Chip(load ? Cyan : _amber, load ? "Load" : "Drop");
        chip.Margin = new Thickness(0, 5, 12, 5);
        chip.VerticalAlignment = VerticalAlignment.Center;
        AddCell(table, row, 1, chip);

        var com = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(commodity) ? "Cargo" : commodity, FontFamily = Mono, FontSize = 11.5,
            Foreground = Br("FgDimBrush"), Margin = new Thickness(0, 5, 8, 5), VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        };
        AddCell(table, row, 2, com);

        var scuTb = new TextBlock
        {
            Text = scu.ToString("N0"), FontFamily = Mono, FontSize = 12, FontWeight = FontWeights.SemiBold,
            Foreground = Br("CyanBrush"), TextAlignment = TextAlignment.Right, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 5, 0, 5), VerticalAlignment = VerticalAlignment.Center,
        };
        AddCell(table, row, 3, scuTb);

        return row + 1;
    }

    private static void AddCell(Grid g, int row, int col, UIElement el)
    {
        Grid.SetRow(el, row); Grid.SetColumn(el, col); g.Children.Add(el);
    }

    private UIElement HeaderCell(string text, bool right) => new TextBlock
    {
        Text = text, FontFamily = Mono, FontSize = 9.5, FontWeight = FontWeights.Bold, Foreground = Br("FgDimBrush"),
        Margin = new Thickness(0, 0, right ? 0 : 8, 8),
        TextAlignment = right ? TextAlignment.Right : TextAlignment.Left,
        HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left,
    };

    // -- finished hauls ------------------------------------------------------------

    private Grid BuildFinishedPanel()
    {
        var finished = App.Hauls.FinishedHauls;

        var bodyStack = new StackPanel();
        bodyStack.Children.Add(PanelHeaderBar($"Finished hauls · {finished.Count}", null));

        var rows = new StackPanel { Margin = new Thickness(14, 4, 10, 12) };
        foreach (var h in finished)
            rows.Children.Add(FinishedRow(h));
        bodyStack.Children.Add(rows);

        return Hud.Panel(bodyStack, padding: new Thickness(0));
    }

    private UIElement FinishedRow(Haul h)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var name = new TextBlock
        {
            Text = Contractor(h), FontFamily = Head, FontSize = 12.5, FontWeight = FontWeights.SemiBold,
            Foreground = Br("FgBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 6, 8, 6),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(name, 0); grid.Children.Add(name);

        var noteText = string.IsNullOrWhiteSpace(h.RouteTitle) ? h.Topology : h.RouteTitle;
        var note = new TextBlock
        {
            Text = noteText, FontFamily = Mono, FontSize = 11, Foreground = Br("FgDimBrush"),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 6, 12, 6), TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(note, 1); grid.Children.Add(note);

        // Complete = green chip; any other ending (Abandoned/Failed/Deactivated) reads as an amber warning.
        var complete = h.Outcome == HaulOutcome.Complete;
        var chip = Hud.Chip(complete ? _green : _amber, h.Outcome.ToString().ToUpperInvariant());
        chip.VerticalAlignment = VerticalAlignment.Center;
        chip.Margin = new Thickness(0, 0, 8, 0);
        Grid.SetColumn(chip, 2); grid.Children.Add(chip);

        var del = DeleteButton(h.MissionId);
        del.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(del, 3); grid.Children.Add(del);

        return grid;
    }

    // -- helpers -------------------------------------------------------------------

    private static string CompanyOf(Haul h) => string.IsNullOrWhiteSpace(h.Company) ? "Unknown company" : h.Company;
    // Prefer OCR-sourced ContractedBy over the generator-derived company name when present.
    private static string Contractor(Haul h) => string.IsNullOrWhiteSpace(h.ContractedBy) ? CompanyOf(h) : h.ContractedBy;

    // Small bordered action button, mirroring NetworkPage.ActionButton.
    private Button ActionButton(string text) => new()
    {
        Content = text, Padding = new Thickness(12, 6, 12, 6),
        Background = Br("Bg2NavBrush"), Foreground = Br("AccentBrush"),
        BorderBrush = Br("NavBorderBrush"), BorderThickness = new Thickness(1),
        Cursor = Cursors.Hand, FontWeight = FontWeights.SemiBold, FontSize = 12,
    };

    // Flat "x" affordance that deletes a single haul. A plain character, not an icon/emoji.
    private Button DeleteButton(string missionId)
    {
        var btn = new Button
        {
            Content = "x", FontFamily = Mono, FontSize = 14, FontWeight = FontWeights.Bold,
            Foreground = Br("FgDimBrush"), Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 2, 8, 2), Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Top,
        };
        btn.Click += (_, _) => App.Hauls.Remove(missionId);   // Changed -> Refresh() rebuilds the list
        return btn;
    }

    // Section kicker rendered as a command-center teal eyebrow.
    private UIElement SectionHeader(string text) => new TextBlock
    {
        Text = text.ToUpperInvariant(), FontFamily = Head, FontSize = 10.5, FontWeight = FontWeights.Bold,
        Foreground = Br("AccentBrush"), Margin = new Thickness(2, 4, 0, 10),
    };

    // In-panel header bar: title (+ optional right-aligned sub) over a hairline divider.
    private UIElement PanelHeaderBar(string title, string? sub)
    {
        var wrap = new StackPanel();
        var bar = new Grid { Margin = new Thickness(14, 11, 14, 9) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var t = new TextBlock
        {
            Text = title.ToUpperInvariant(), FontFamily = Head, FontSize = 12.5, FontWeight = FontWeights.Bold,
            Foreground = Br("FgBrush"), VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(t, 0); bar.Children.Add(t);

        if (!string.IsNullOrWhiteSpace(sub))
        {
            var s = new TextBlock
            {
                Text = sub, FontSize = 10.5, Foreground = Br("FgDimBrush"), VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(s, 1); bar.Children.Add(s);
        }
        wrap.Children.Add(bar);
        wrap.Children.Add(new Border { Height = 1, Background = Br("NavBorderBrush") });
        return wrap;
    }

    // Small progress pip: filled teal when the step is done, hollow outline while pending.
    private UIElement StatusDot(bool done) => new Border
    {
        Width = 7, Height = 7, CornerRadius = new CornerRadius(4),
        Background = done ? Br("AccentBrush") : Brushes.Transparent,
        BorderBrush = done ? Br("AccentBrush") : Br("NavBorderBrush"), BorderThickness = new Thickness(1.5),
        VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 9, 0),
    };

    private UIElement MutedLine(string text) => new TextBlock
    {
        Text = text, FontSize = 12, Foreground = Br("FgDimBrush"),
        Margin = new Thickness(2, 2, 0, 6), TextWrapping = TextWrapping.Wrap,
    };

    // Empty state rendered as a centered chamfered HUD panel rather than bare text.
    private UIElement Placeholder(string text)
    {
        var tb = new TextBlock
        {
            Text = text, Foreground = Br("FgDimBrush"), FontSize = 13, TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center,
        };
        var panel = Hud.Panel(tb, brackets: true, padding: new Thickness(28));
        panel.Margin = new Thickness(0, 8, 0, 0);
        return panel;
    }
}
