using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NexusApp.Models.Cargo;
using NexusApp.Services;
using NexusApp.Services.Cargo;
using static NexusApp.Views.CargoUiKit;

namespace NexusApp.Views;

// Owner-only import-review panel for Grid Studio. Loads a contributor's .nexusgrid submission (validated
// by the host), lets the owner pick which aspects to bring in (positions / sizes / caps / roster),
// previews the merged result in the 3D viewport, and raises Applied / Discarded. It owns NO stores or
// catalog: the host injects currentFor (the owner's current grids for a ship, recomputed on demand so a
// mid-review save cannot be silently overwritten) and buildPreview (the single BuildGrid validation gate
// stays in the catalog). All persistence happens in the host's Applied handler.
internal sealed class GridImportReviewPanel : StackPanel
{
    private readonly Func<string, List<GridOverride>> _currentFor;
    private readonly Func<string, IReadOnlyList<GridOverride>, ShipCargoDef?> _buildPreview;

    private readonly TextBlock _flagLine = new();   // shown (Warn) only when the submission carries a flag
    private readonly TextBlock _meta = new();
    private readonly TextBlock _diff = new();
    private readonly Dictionary<GridMergeAspect, Button> _aspectBtns = new();

    // Holds the submission package (its grids + provenance) and the target ship's display name. The
    // owner's Current grids are recomputed via _currentFor at preview/apply time so a save made while the
    // panel is open is never silently overwritten.
    private (GridSharePackage Pkg, string ShipName)? _pending;
    private GridMergeAspect _aspects = GridMergeAspect.All;

    // The submission aspects the owner can independently bring in, shown as toggles.
    private static readonly (GridMergeAspect Aspect, string Label)[] AspectRows =
    {
        (GridMergeAspect.Positions, "Grid positions"),
        (GridMergeAspect.Sizes, "Grid sizes"),
        (GridMergeAspect.Caps, "Container caps"),
        (GridMergeAspect.GridSet, "Added / removed grids"),
    };

    // Applied: the owner accepted a merge - args are (shipId, shipName, merged grid set, chosen aspects,
    // the source submission for provenance).
    public event Action<string, string, List<GridOverride>, GridMergeAspect, GridSharePackage>? Applied;
    // Discarded: the owner dismissed the review - arg is the ship name (for the host's status line).
    public event Action<string>? Discarded;
    // Ask the host to render this preview (the merged result) in its viewport, non-destructively.
    public event Action<ShipCargoDef>? PreviewRequested;
    // A panel-side status message (apply blocked, nothing selected) - (message, isWarn).
    public event Action<string, bool>? Status;

    public bool IsActive => _pending != null;
    public string? ActiveShipId => _pending?.Pkg.ShipId;

    public GridImportReviewPanel(
        Func<string, List<GridOverride>> currentFor,
        Func<string, IReadOnlyList<GridOverride>, ShipCargoDef?> buildPreview)
    {
        _currentFor = currentFor;
        _buildPreview = buildPreview;
        Visibility = Visibility.Collapsed;
        Margin = new Thickness(0, 0, 0, 14);
        Build();
    }

    private void Build()
    {
        Children.Add(Eyebrow("IMPORT REVIEW"));
        _flagLine.FontFamily = Mono; _flagLine.FontSize = 11; _flagLine.TextWrapping = TextWrapping.Wrap;
        _flagLine.Foreground = new SolidColorBrush(Warn); _flagLine.Margin = new Thickness(0, 6, 0, 0);
        _flagLine.Visibility = Visibility.Collapsed;
        Children.Add(_flagLine);
        _meta.FontFamily = Mono; _meta.FontSize = 11; _meta.TextWrapping = TextWrapping.Wrap;
        _meta.Foreground = new SolidColorBrush(Fg); _meta.Margin = new Thickness(0, 6, 0, 6);
        Children.Add(_meta);
        _diff.FontFamily = Mono; _diff.FontSize = 11; _diff.TextWrapping = TextWrapping.Wrap;
        _diff.Foreground = new SolidColorBrush(Dim); _diff.Margin = new Thickness(0, 0, 0, 8);
        Children.Add(_diff);
        // Per-aspect selection: pick which parts of the submission to bring in (vs keep current). The 3D
        // preview updates live to show the merged result as toggles change.
        Children.Add(new TextBlock
        {
            Text = "Bring in:", Foreground = new SolidColorBrush(Dim), FontFamily = Mono, FontSize = 10.5,
            Margin = new Thickness(0, 2, 0, 4),
        });
        foreach (var (aspect, label) in AspectRows)
        {
            var b = AspectBtn(aspect, label);
            _aspectBtns[aspect] = b;
            Children.Add(b);
        }
        var btns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        btns.Children.Add(Btn("Apply selected", (_, _) => Apply(), primary: true));
        btns.Children.Add(Btn("Discard", (_, _) => Discard()));
        Children.Add(btns);
    }

    // Open a review for a submission the host has already validated (built a preview and computed the
    // diff against current). Grid ids are positional, so when the submission's roster differs from the
    // owner's, force whole-roster (GridSet) and lock the per-field toggles (a per-field merge would land
    // on the wrong physical grid).
    public void Begin(GridSharePackage pkg, ShipCargoDef targetShip, List<GridOverride> current, GridDiffResult diff)
    {
        _pending = (pkg, targetShip.DisplayName);

        bool rosterDiffers = !current.Select(g => g.Id).OrderBy(i => i)
            .SequenceEqual(pkg.Grids.Select(g => g.Id).OrderBy(i => i));
        _aspects = rosterDiffers ? GridMergeAspect.GridSet : GridMergeAspect.All;
        ConfigureAspectAvailability(rosterDiffers);
        UpdateAspectButtons();

        var handle = string.IsNullOrWhiteSpace(pkg.RsiHandle) ? "(no handle)" : pkg.RsiHandle;
        var nameNote = string.Equals(pkg.ShipName, targetShip.DisplayName, StringComparison.OrdinalIgnoreCase)
            ? "" : $"\nWARNING: the file calls this '{pkg.ShipName}'";
        _meta.Text = $"From: {handle}\nShip: {targetShip.DisplayName}{nameNote}\nSummary: {pkg.Summary}\nNotes: {pkg.Notes}";
        _diff.Text = "Changes vs your version:\n  " + string.Join("\n  ", diff.Lines)
            + (rosterDiffers
                ? "\n(grid roster differs; bringing in the whole set)"
                : diff.HasChanges ? "\n(per-grid aspects match by grid number; verify against the preview)" : "");
        if (pkg.Flagged)
        {
            _flagLine.Text = "CONTRIBUTOR FLAG: " + (string.IsNullOrWhiteSpace(pkg.FlagNote) ? "(no note)" : pkg.FlagNote);
            _flagLine.Visibility = Visibility.Visible;
        }
        else _flagLine.Visibility = Visibility.Collapsed;
        Visibility = Visibility.Visible;
        RefreshPreview();
    }

    // Cancel an open review without applying (e.g. the host switched ships). No events raised.
    public void Cancel()
    {
        _pending = null;
        Visibility = Visibility.Collapsed;
    }

    // Re-render the merged (current + selected aspects) preview. Public so the host can keep the preview
    // on screen when it would otherwise re-enter the editor.
    public void RefreshPreview()
    {
        if (_pending is not { } imp) return;
        var merged = GridMerge.Apply(_currentFor(imp.Pkg.ShipId), imp.Pkg.Grids, _aspects);
        ShipCargoDef? preview = null;
        try { preview = _buildPreview(imp.Pkg.ShipId, merged); }
        catch (Exception ex) { Logger.Error("Cargo grid import preview failed", ex); }
        if (preview == null) return;
        PreviewRequested?.Invoke(preview);
    }

    private void ToggleAspect(GridMergeAspect aspect)
    {
        _aspects ^= aspect;
        UpdateAspectButtons();
        RefreshPreview();
    }

    private void UpdateAspectButtons()
    {
        foreach (var (aspect, label) in AspectRows)
        {
            if (!_aspectBtns.TryGetValue(aspect, out var b)) continue;
            bool on = _aspects.HasFlag(aspect);
            b.Content = (on ? "[x]  " : "[  ]  ") + label;
            b.Foreground = new SolidColorBrush(!b.IsEnabled ? Line : on ? Cyan : Dim);
            b.Background = new SolidColorBrush(on ? Color.FromRgb(0x0D, 0x24, 0x22) : Color.FromRgb(0x14, 0x1B, 0x24));
            b.BorderBrush = new SolidColorBrush(on ? Cyan : Line);
        }
    }

    // Enable only the whole-roster toggle when the submission's grid roster differs from the owner's
    // (per-field selection is positional and would misapply); otherwise all toggles are available.
    private void ConfigureAspectAvailability(bool rosterDiffers)
    {
        foreach (var (aspect, _) in AspectRows)
            if (_aspectBtns.TryGetValue(aspect, out var b))
                b.IsEnabled = !rosterDiffers || aspect == GridMergeAspect.GridSet;
    }

    private Button AspectBtn(GridMergeAspect aspect, string label)
    {
        var b = new Button
        {
            Padding = new Thickness(9, 4, 9, 4),
            Margin = new Thickness(0, 0, 0, 3),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Cursor = System.Windows.Input.Cursors.Hand,
            FontFamily = Mono, FontSize = 11,
            BorderThickness = new Thickness(1),
        };
        b.Click += (_, _) => ToggleAspect(aspect);
        return b;
    }

    private void Apply()
    {
        if (_pending is not { } imp) return;
        var current = _currentFor(imp.Pkg.ShipId);
        var merged = GridMerge.Apply(current, imp.Pkg.Grids, _aspects);

        // Nothing selected, or the merge equals what you already have: do not persist an override (which
        // would freeze the ship against future embedded refreshes) or clear the sign-off.
        if (_aspects == GridMergeAspect.None || GridsEqual(merged, current))
        {
            Status?.Invoke("No changes selected; nothing applied.", true);
            return;
        }

        // Never accept a physically impossible grid (a caps-only merge can accept a container the kept
        // dimensions cannot hold). BuildPreview validates dimensions/positions; this adds the fit check.
        ShipCargoDef? built;
        try { built = _buildPreview(imp.Pkg.ShipId, merged); }
        catch (Exception ex) { Status?.Invoke($"Cannot apply: {ex.Message}", true); return; }
        if (built == null) { Status?.Invoke("Cannot apply: ship is not in the catalog.", true); return; }
        var problem = GridValidation.FirstProblem(built.Grids);
        if (problem != null) { Status?.Invoke($"Cannot apply: {problem}.", true); return; }

        var (pkg, shipName, aspects) = (imp.Pkg, imp.ShipName, _aspects);
        _pending = null;
        Visibility = Visibility.Collapsed;
        Applied?.Invoke(pkg.ShipId, shipName, merged, aspects, pkg);
    }

    private void Discard()
    {
        var name = _pending?.ShipName ?? "";
        _pending = null;
        Visibility = Visibility.Collapsed;
        Discarded?.Invoke(name);
    }

    // Value equality of two grid sets, matched by Id, order-insensitive (Accepts compared by contents).
    private static bool GridsEqual(List<GridOverride> a, List<GridOverride> b)
    {
        if (a.Count != b.Count) return false;
        var sa = a.OrderBy(g => g.Id).ToList();
        var sb = b.OrderBy(g => g.Id).ToList();
        for (int i = 0; i < sa.Count; i++)
        {
            var x = sa[i]; var y = sb[i];
            if (x.Id != y.Id || x.W != y.W || x.D != y.D || x.H != y.H || x.Cap != y.Cap ||
                x.Px != y.Px || x.Py != y.Py || x.Pz != y.Pz || x.Wy != y.Wy) return false;
            var ax = (x.Accepts ?? new List<int>()).OrderBy(v => v);
            var ay = (y.Accepts ?? new List<int>()).OrderBy(v => v);
            if (!ax.SequenceEqual(ay)) return false;
        }
        return true;
    }
}
