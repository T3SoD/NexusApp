using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using NexusApp.Services;

namespace NexusApp.Views;

// Shared commodity field for the Sell and Prices flows (issue #41): one TextBox the user can TYPE
// into (CommoditySuggest.Filter refilters the popup per keystroke) plus a chevron that BROWSES the
// full list - the two tabs previously had one affordance each (Prices a plain dropdown, Sell a
// type-only inline picker). Code-built composite, matching the trade page's own idiom; NOT a
// NexusComboBox variant - that style's template has no PART_EditableTextBox, so IsEditable renders
// no typable area, and the style is shared with other pages and stays untouched.
//
// Commit contract: Committed fires ONLY from a row click (MouseLeftButtonUp). Typing never commits
// and never clears a caller's selection; programmatic Text writes are suppressed so they are never
// read as the user typing. There is NO key handler anywhere here, and focus never tears the popup
// down: the popup is StaysOpen=false, so an outside mouse-down closes it via the popup's own mouse
// capture, and a pending row click can never race a focus-driven teardown (the ordering guarantee
// the Sell flow's old inline picker documented). The one focus handler (LostKeyboardFocus, below)
// only reports InteractionEnded - it never closes the popup, changes text, or commits.
internal sealed class CommodityPickerBox : Grid
{
    private readonly TextBox _box;
    private readonly Popup _popup;
    private readonly StackPanel _list;
    private IReadOnlyList<string> _names = Array.Empty<string>();
    private bool _suppressText;   // programmatic Text writes are not the user typing
    private bool _committing;     // a row click is mid-commit: its popup close is not the user walking away

    /// <summary>Raised only by a row click - the single commit path. Callers log and rebuild.</summary>
    public event Action<string>? Committed;

    /// <summary>Raised only when the chevron opens the popup (not on typing), so each tab can
    /// write its own "[UI] ... commodity list opened" log line without per-keystroke logging.</summary>
    public event Action? Opened;

    /// <summary>Raised when the user walks away without committing: keyboard focus leaves the box
    /// while the popup is closed, or an outside mouse-down dismisses the popup (keyboard focus can
    /// then linger in the box for the rest of a pane visit when nothing else there is focusable,
    /// so the dismissal itself must count as the end). Never raised by a row commit, a programmatic
    /// close, or a no-match close mid-typing. Subscribers use it to revert abandoned query text to
    /// the selection actually rendered. Focus events, not key handling - the mouse-only rule holds.</summary>
    public event Action? InteractionEnded;

    public CommodityPickerBox()
    {
        _box = new TextBox
        {
            Style = (Style)Application.Current.FindResource("NexusTextBox"),
            Padding = new Thickness(10, 8, 30, 8),   // right inset clears the chevron, NexusComboBox's own content margin
            Tag = "Type or browse",                  // NexusTextBox renders Tag as the empty-state placeholder
        };
        _box.TextChanged += (_, _) => { if (!_suppressText) RefreshPopup(_box.Text ?? "", viaChevron: false); };

        _list = new StackPanel();
        _popup = new Popup
        {
            PlacementTarget = _box, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true,
            Child = new Border
            {
                Background = Hud.Br("Bg2NavBrush"), BorderBrush = Hud.Br("NavBorderBrush"), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(0, 0, 6, 6),
                Child = new ScrollViewer { MaxHeight = 200, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _list },
            },
        };

        var chevronHost = new Border
        {
            Width = 24, Background = Brushes.Transparent, Cursor = Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Right, Child = ChevronGlyphDown(),
        };
        // Toggle on DOWN, not up, mirroring NexusComboBox's own ClickMode=Press: while the popup is
        // open it holds the mouse capture (StaysOpen=false), so the down that lands here is eaten
        // by the capture and closes the popup WITHOUT reaching this handler - that IS the "click
        // chevron to close" half of the toggle. An up handler would fire after that capture-close
        // and immediately reopen the popup the same click just dismissed.
        chevronHost.MouseLeftButtonDown += (_, e) => { e.Handled = true; RefreshPopup("", viaChevron: true); };

        // InteractionEnded wiring (see the event doc). The Closed check reads the live mouse-button
        // state: a StaysOpen=false dismissal fires Closed synchronously inside the outside
        // mouse-down, so a pressed button distinguishes "user clicked elsewhere" from programmatic
        // closes (SetTextSuppressed never closes; ClosePopup and the no-match close arrive with no
        // button down and, mid-typing, with the box still focused).
        _popup.Closed += (_, _) =>
        {
            if (_committing) return;
            if (Mouse.LeftButton == MouseButtonState.Pressed || Mouse.RightButton == MouseButtonState.Pressed
                || !_box.IsKeyboardFocused)
                InteractionEnded?.Invoke();
        };
        _box.LostKeyboardFocus += (_, _) => { if (!_popup.IsOpen) InteractionEnded?.Invoke(); };

        // A picker that leaves the screen takes its popup with it: the popup is its own HWND, so a
        // ghost-collapse or tab switch hiding the host panel would otherwise leave the list
        // floating over the game. Benign on the desktop pages (their pickers hide only on page
        // switches, where an open popup is equally wrong).
        IsVisibleChanged += (_, _) => { if (!IsVisible) _popup.IsOpen = false; };

        Children.Add(_box);
        Children.Add(chevronHost);
        Children.Add(_popup);
    }

    /// <summary>The box's live text. Setting is a programmatic write: suppressed (never reopens
    /// the popup or fires Committed), and leaves any open popup alone - closing is the caller's
    /// explicit call (ClosePopup) or the popup's own outside-click capture.</summary>
    public string Text
    {
        get => _box.Text;
        set => SetTextSuppressed(value);
    }

    /// <summary>Optional sentinel row (the planner's "ANY") pinned above the alphabetical list in
    /// browse mode and matched like any name when typing. Never part of SetItems' list; a click on
    /// it commits through the same path as every other row. Superseded by PinnedItems when that is
    /// set (non-null) - see PinnedItems below.</summary>
    public string? PinnedFirst { get; set; }

    /// <summary>Optional pinned ROW LIST (overlay planner spec R2, Task B: the start picker pins
    /// "ANY" and "LIVE" together, in that order, above the priced terminal names) - the
    /// generalization of PinnedFirst's single sentinel, backed by CommoditySuggest.Filter's list
    /// overload. Wins over PinnedFirst whenever it is set (non-null), even an empty list; each row
    /// keeps its given order, leads the alphabetical list in browse mode, and is matched like any
    /// other name when typing. Never part of SetItems' list; a click on a pinned row commits
    /// through the same path as every other row.</summary>
    public IReadOnlyList<string>? PinnedItems { get; set; }

    /// <summary>True while the user is mid-interaction (box keyboard-focused or popup open) - the
    /// window in which callers must not push list updates or write the text back (the Prices
    /// flow's defer-to-next-rebuild rule).</summary>
    public bool IsInteracting => _box.IsKeyboardFocused || _popup.IsOpen;

    /// <summary>Overlay mode (owner's live-pass find, 2026-08-02: overlay dropdowns collapsed the
    /// instant they were clicked). In a Topmost window, StaysOpen=false's capture-based auto-close
    /// misfires - the activation/capture churn of clicking the overlay dismisses the popup within
    /// the same click. When true, the popup opens with StaysOpen=true and the HOST window owns
    /// outside-click dismissal: it must close still-open popups from its own PreviewMouseDown when
    /// the pointer is over neither the picker nor its popup (see IsMouseOverPopup). The desktop
    /// pages keep the default false and the original StaysOpen=false behavior.</summary>
    public bool HostManagedClose { get; set; }

    /// <summary>Whether the pointer is currently over the OPEN popup's content - the popup is its
    /// own visual tree, so the host's IsMouseOver never covers it. Host-managed dismissal checks
    /// this plus the picker's own IsMouseOver before closing.</summary>
    public bool IsMouseOverPopup => _popup.IsOpen && _popup.Child is FrameworkElement c && c.IsMouseOver;

    /// <summary>True while the suggestion popup is open.</summary>
    public bool IsPopupOpen => _popup.IsOpen;

    /// <summary>Replaces the backing name list. Deliberately does NOT touch an already-open
    /// popup's rows: they refilter on the next keystroke or chevron open, so a mid-interaction
    /// snapshot tick can never rebuild rows out from under a pending click.</summary>
    public void SetItems(IReadOnlyList<string> names) => _names = names;

    public void ClosePopup() => _popup.IsOpen = false;

    private void SetTextSuppressed(string value)
    {
        _suppressText = true;
        try { _box.Text = value; } finally { _suppressText = false; }
    }

    private void RefreshPopup(string query, bool viaChevron)
    {
        // Defensive close half of the chevron toggle: normally unreachable (the capture-close above
        // means an open popup never lets the chevron handler fire), kept so the toggle stays a
        // toggle even if that routing ever changes.
        if (viaChevron && _popup.IsOpen) { _popup.IsOpen = false; return; }

        // PinnedItems wins over PinnedFirst when set (its own doc comment's contract) - falls back
        // to wrapping the single sentinel into a one-row list so both properties funnel through
        // the same Filter overload.
        var pinned = PinnedItems ?? (PinnedFirst is null ? null : new[] { PinnedFirst });
        var matches = CommoditySuggest.Filter(_names, query, pinned);
        if (matches.Count == 0) { _popup.IsOpen = false; return; }

        _list.Children.Clear();
        foreach (var name in matches) _list.Children.Add(SuggestRow(name));

        // Popups render in their own PopupRoot HWND outside the app-scale LayoutTransform, so the
        // logical width must be pre-multiplied AND the content scaled to match (the Blueprints
        // suggest popup's rule, MainWindow.Blueprints.cs / MainWindow.ApplyUiScale). Applied per
        // open so a scale change never needs a subscription here.
        var k = UiScaleService.AppScale;
        if (_popup.Child is FrameworkElement child) UiScaleService.ApplyTransform(child, k);
        _popup.Width = _box.ActualWidth * k;

        // Applied per open so the flag can be set any time after construction. See HostManagedClose:
        // the overlay popup must not auto-close via capture, the host dismisses it instead.
        _popup.StaysOpen = HostManagedClose;

        bool wasOpen = _popup.IsOpen;
        _popup.IsOpen = true;
        if (viaChevron && !wasOpen) Opened?.Invoke();
    }

    // Row visuals verbatim from the Sell flow's old inline picker (padding, font, hover tint);
    // commit on MouseLeftButtonUp only - see the class comment's ordering guarantee.
    private Border SuggestRow(string name)
    {
        var row = new Border
        {
            Padding = new Thickness(12, 8, 12, 8), Cursor = Cursors.Hand, Background = Brushes.Transparent,
            Child = new TextBlock { Text = name, FontFamily = Hud.Font("UiFont"), FontSize = 12.5, Foreground = Hud.Br("FgBrush") },
        };
        row.MouseEnter += (_, _) => row.Background = Hud.Br("AccentFaintBrush");
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        row.MouseLeftButtonUp += (_, _) =>
        {
            SetTextSuppressed(name);
            _committing = true;
            try { _popup.IsOpen = false; } finally { _committing = false; }
            Committed?.Invoke(name);
        };
        return row;
    }

    // House chevron Path (ChevronGlyph's exact glyph data, TradePage.Planner.cs), rotated to point
    // down - the browse affordance NexusComboBox's toggle shows. Never a text character.
    private static Path ChevronGlyphDown() => new()
    {
        Width = 9, Height = 9, Data = Geometry.Parse("M5,3 L11,8 L5,13"),
        Stroke = Hud.Br("FgDimBrush"), StrokeThickness = 1.6, StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round, Fill = Brushes.Transparent,
        Stretch = Stretch.Uniform, RenderTransformOrigin = new Point(0.5, 0.5),
        RenderTransform = new RotateTransform(90),
        VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
        IsHitTestVisible = false,   // the transparent host takes the click, glyph is pure visual
    };
}
