using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using NexusApp.Services;

namespace NexusApp.Views;

/// <summary>
/// The Mission Guides dock page (code-built, like HaulingPage): the curated guide catalog as a
/// category-grouped card grid, with the shared <see cref="GuideViewer"/> taking over the whole
/// page when a card is clicked. Mouse only, as the app is throughout.
///
/// The page owns ONE viewer instance, so at most one decoded guide bitmap is alive; Back (and
/// navigating away from the page) releases it. Layout values come from the approved framer mock
/// (nexus-design-lab\guides\index.html) and its value manifest.
/// </summary>
public sealed class GuidesPage : UserControl
{
    // Cascade idiom shared with the rest of the app (MainWindow CascadeIn): 200ms per element,
    // 40ms stagger, 12px rise, quad-out. Section heads and cards share one continuous index so
    // the page sweeps in as a single movement; the credits footer is last.
    private const double CascadeMs = 200;
    private const double CascadeStepMs = 40;
    private const double RisePx = 12;
    // Vertical read of the dock tile's 3px hover slide (GameTheme.xaml DockTile).
    private const double HoverLiftPx = 3;

    private const double CardWidth = 248;
    private const double ThumbHeight = 130;
    private const int ThumbDecodeWidth = 400;   // spec "Memory and decode strategy"

    private readonly Grid _root = new();
    // Named so InteractionLog can report a region for the card clicks.
    private readonly Grid _listHost = new() { Name = "GuidesList" };
    private readonly StackPanel _body = new();
    private readonly GuideViewer _viewer = new(compact: false);

    // Section heads and cards in cascade order, credits appended last.
    private readonly List<FrameworkElement> _cascade = new();

    private GuideEntry? _openGuide;

    // Executive Hangar status line (issue #26): live PYAM contested-zone hangar cycle, slotted
    // into the Contested Zones section head. One DispatcherTimer owned by the page, started and
    // stopped from the IsVisibleChanged handler below (GuidesPage is a lazy singleton kept
    // permanently in MainWindow's tree; page switches are pure Visibility toggling, which never
    // fires Loaded/Unloaded - see HangarPageEntered/HangarPageLeft).
    private DispatcherTimer? _hangarTimer;
    private readonly Ellipse[] _hangarDots = new Ellipse[5];
    private StackPanel? _hangarLine;
    private TextBlock? _hangarPhaseText;
    private TextBlock? _hangarVerbText;
    private TextBlock? _hangarCountdownText;
    private TextBlock? _hangarTailText;
    private TextBlock? _hangarResetLink;
    private TextBlock? _hangarNextOpensText;

    private const double HangarDotSize = 7;
    private const double HangarDotGap = 4;
    private const double HangarItemGap = 6;

    public GuidesPage()
    {
        BuildList();

        _viewer.Visibility = Visibility.Collapsed;
        _viewer.BackRequested += (_, _) => CloseGuide(replayCascade: true);

        _root.Children.Add(_listHost);
        _root.Children.Add(_viewer);
        Content = _root;

        // Leaving the page while a guide is open must not park a full-size bitmap in memory
        // (the largest guide decodes to about 116 MB).
        IsVisibleChanged += (_, _) => { if (!IsVisible && _openGuide != null) CloseGuide(replayCascade: false); };

        // IsVisible correctly reflects an ancestor's Visibility toggling (unlike Loaded/Unloaded,
        // which never fire for a lazy-singleton page whose host just flips Visibility - see the
        // field comment on _hangarTimer), so it drives the hangar timer's start/stop on every
        // entry and exit, not just the first one.
        IsVisibleChanged += (_, _) => { if (IsVisible) HangarPageEntered(); else HangarPageLeft(); };
    }

    /// <summary>Called by MainWindow every time the dock activates this page.</summary>
    public void Activate()
    {
        Logger.Info("[UI] guides page opened");
        if (_openGuide != null) CloseGuide(replayCascade: false);
        PlayCascade();
    }

    // -- guide open / close ----------------------------------------------------------

    private void OpenGuide(GuideEntry guide, DependencyObject source)
    {
        InteractionLog.Click(guide.Title, source);
        _openGuide = guide;
        _listHost.Visibility = Visibility.Collapsed;
        _viewer.Visibility = Visibility.Visible;
        _viewer.Show(guide);
        Logger.Info($"[UI] guide opened: {guide.Id} (main)");
    }

    private void CloseGuide(bool replayCascade)
    {
        var id = _openGuide?.Id;
        _openGuide = null;
        _viewer.Clear();                    // releases the decoded bitmap
        _viewer.Visibility = Visibility.Collapsed;
        _listHost.Visibility = Visibility.Visible;
        if (id != null) Logger.Info($"[UI] guide closed: {id}");
        if (replayCascade) PlayCascade();
    }

    // -- list layout -----------------------------------------------------------------

    private void BuildList()
    {
        _listHost.Margin = new Thickness(20, 16, 20, 16);   // page root padding (HaulingPage)
        _listHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _listHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = Hud.Header("REFERENCE", "Mission Guides",
            "Curated maps and tactical references. Open one for a full zoom and pan view.");
        Grid.SetRow(header, 0);
        _listHost.Children.Add(header);

        foreach (var category in GuideCatalog.Categories)
            _body.Children.Add(BuildSection(category));

        var credits = BuildCredits();
        _body.Children.Add(credits);
        _cascade.Add(credits);              // last element of the page cascade

        var scroller = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _body,
        };
        Grid.SetRow(scroller, 1);
        _listHost.Children.Add(scroller);
    }

    // One category: label + count chip + hairline rule, then the wrapped card grid.
    private UIElement BuildSection(string category)
    {
        var guides = new List<GuideEntry>(GuideCatalog.ByCategory(category));

        // 22px between sections in the mock, less the 10px bottom margin the cards already carry.
        var section = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };

        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var label = new TextBlock
        {
            Text = category.ToUpperInvariant(),
            Style = (Style)Application.Current.FindResource("SectionLabel"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(label, 0);
        head.Children.Add(label);

        // Count chip in the dock-head language: mono amber inside a 1px hairline.
        var chip = new Border
        {
            BorderBrush = Hud.Br("NavBorderBrush"), BorderThickness = new Thickness(1),
            Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(9, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = guides.Count.ToString("00"),
                FontFamily = Hud.Font("MonoFont"), FontSize = 10, Foreground = Hud.Br("AccentBrush"),
            },
        };
        Grid.SetColumn(chip, 1);
        head.Children.Add(chip);

        // Contested Zones carries the Executive Hangar status line in place of the hairline rule
        // (issue #26); every other category keeps the plain rule exactly as before.
        var isContestedZones = category == "Contested Zones";
        if (isContestedZones)
        {
            var status = BuildHangarStatusLine();
            Grid.SetColumn(status, 2);
            head.Children.Add(status);
        }
        else
        {
            var rule = new Border
            {
                Height = 1, Background = Hud.Br("NavBorderBrush"),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(9, 0, 0, 0),
            };
            Grid.SetColumn(rule, 2);
            head.Children.Add(rule);
        }

        section.Children.Add(head);
        _cascade.Add(head);

        if (isContestedZones)
        {
            var nextOpens = BuildHangarNextOpensLine();
            section.Children.Add(nextOpens);
            _cascade.Add(nextOpens);
            RefreshHangarStatus();   // paint the initial state; IsVisibleChanged drives it from here
        }

        var grid = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        foreach (var guide in guides)
        {
            var card = BuildCard(guide);
            grid.Children.Add(card);
            _cascade.Add(card);
        }
        section.Children.Add(grid);
        return section;
    }

    // -- card ------------------------------------------------------------------------

    private FrameworkElement BuildCard(GuideEntry guide)
    {
        var inner = new StackPanel();

        var thumbHost = new Grid
        {
            Height = ThumbHeight, ClipToBounds = true, Background = Hud.Br("BgBrush"),
        };
        var image = new Image { Stretch = Stretch.UniformToFill };   // cover-crop
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        var thumb = Thumbnail(guide);
        if (thumb != null)
        {
            image.Source = thumb;
            thumbHost.Children.Add(image);
        }
        else
        {
            thumbHost.Children.Add(new TextBlock
            {
                Text = "Image unavailable", FontFamily = Hud.Font("UiFont"), FontSize = 11,
                Foreground = Hud.Br("FgDimBrush"),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            });
        }

        // Scrim so the title block reads over bright map art.
        var scrimBrush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        scrimBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0x05, 0x07, 0x0A), 0.45));
        scrimBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0xB8, 0x05, 0x07, 0x0A), 1.0));
        scrimBrush.Freeze();
        thumbHost.Children.Add(new Rectangle { Fill = scrimBrush, IsHitTestVisible = false });

        // Amber edge tick, the card-scale echo of the dock selector bar.
        var tick = new Border
        {
            Width = 2, HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Stretch, Background = Hud.Br("AccentStrongBrush"),
            IsHitTestVisible = false,
        };
        thumbHost.Children.Add(tick);
        inner.Children.Add(thumbHost);

        var meta = new StackPanel { Margin = new Thickness(5, 9, 5, 4) };
        var title = new TextBlock
        {
            Text = guide.Title, FontFamily = Hud.Font("UiFont"), FontSize = 12.5,
            FontWeight = FontWeights.SemiBold, Foreground = Hud.Br("FgDimBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        meta.Children.Add(title);
        meta.Children.Add(new TextBlock
        {
            Text = $"{guide.NativeWidth} x {guide.NativeHeight}",
            FontFamily = Hud.Font("MonoFont"), FontSize = 10, Foreground = Hud.Br("FgDimBrush"),
            Opacity = 0.85, Margin = new Thickness(0, 3, 0, 0),
        });
        inner.Children.Add(meta);

        var host = Hud.CardFrame(inner, out var frame, out var brackets, chamfer: 10, padding: new Thickness(8));
        // House bracket glow (ChamferPanel PART_Brackets); the layer only shows on hover.
        brackets.Effect = new DropShadowEffect
        { Color = Hud.Col("AccentBrush"), BlurRadius = 4, ShadowDepth = 0, Opacity = 0.5 };
        host.Width = CardWidth;
        host.Margin = new Thickness(0, 0, 10, 10);   // the 10px card gap
        host.Cursor = Cursors.Hand;
        var slide = new TranslateTransform();
        host.RenderTransform = slide;

        var tickGlow = new DropShadowEffect
        { Color = Hud.Col("AccentBrush"), BlurRadius = 10, ShadowDepth = 0, Opacity = 0.85 };
        var panelGlow = new DropShadowEffect
        { Color = Hud.Col("AccentBrush"), BlurRadius = 22, ShadowDepth = 0, Opacity = 0.22 };

        host.MouseEnter += (_, _) =>
        {
            frame.Fill = Hud.Br("Bg3Brush");
            frame.Stroke = Hud.Br("AccentStrongBrush");
            brackets.Visibility = Visibility.Visible;
            tick.Background = Hud.Br("AccentBrush");
            tick.Effect = tickGlow;
            title.Foreground = Hud.Br("FgBrush");
            // Reduce animations: the hover keeps its colour change but loses the lift and glow.
            if (Motion.Reduced) return;
            frame.Effect = panelGlow;
            LiftTo(slide, -HoverLiftPx);
        };
        host.MouseLeave += (_, _) =>
        {
            frame.Fill = Hud.Br("Bg2NavBrush");
            frame.Stroke = Hud.Br("NavBorderBrush");
            frame.Effect = null;
            brackets.Visibility = Visibility.Collapsed;
            tick.Background = Hud.Br("AccentStrongBrush");
            tick.Effect = null;
            title.Foreground = Hud.Br("FgDimBrush");
            if (Motion.Reduced) { slide.BeginAnimation(TranslateTransform.YProperty, null); slide.Y = 0; return; }
            LiftTo(slide, 0);
        };
        host.MouseLeftButtonDown += (_, _) => OpenGuide(guide, host);
        return host;
    }

    private static void LiftTo(TranslateTransform slide, double y)
        => slide.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(y, new Duration(TimeSpan.FromMilliseconds(Motion.HoverMs))) { EasingFunction = Motion.SlideOut });

    // Card thumbnails are small enough that all six can live at once (spec "Memory and decode
    // strategy"). A bad or missing resource degrades to the placeholder instead of crashing;
    // log strings carry the guide id only.
    private static BitmapImage? Thumbnail(GuideEntry guide)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(guide.PackUri, UriKind.Absolute);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = ThumbDecodeWidth;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex)
        {
            Logger.Error($"[UI] guide thumbnail unavailable: {guide.Id}", ex);
            return null;
        }
    }

    // -- credits ---------------------------------------------------------------------

    // The one place in the app that names the guide creators (spec decision 6). Log lines and the
    // catalog stay on guide ids.
    private static FrameworkElement BuildCredits()
    {
        var stack = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        stack.Children.Add(new Border { Height = 1, Background = Hud.Br("NavBorderBrush") });
        stack.Children.Add(new TextBlock
        {
            Text = "CREDITS",
            Style = (Style)Application.Current.FindResource("SectionLabel"),
            Margin = new Thickness(0, 10, 0, 0),
        });

        var line = new TextBlock
        {
            FontFamily = Hud.Font("UiFont"), FontSize = 11, Foreground = Hud.Br("FgDimBrush"),
            Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap,
        };
        line.Inlines.Add(new Run("Guide artwork by "));
        line.Inlines.Add(new Run("MrKraken") { Foreground = Hud.Br("FgBrush"), FontWeight = FontWeights.SemiBold });
        line.Inlines.Add(new Run(" and "));
        line.Inlines.Add(new Run("Zand_DragonBorn") { Foreground = Hud.Br("FgBrush"), FontWeight = FontWeights.SemiBold });
        stack.Children.Add(line);
        return stack;
    }

    // -- exec hangar status (issue #26) -----------------------------------------------

    // Horizontal line: 5-dot phase cluster, phase word, countdown, an optional tail note, and
    // the Re-anchor / Reset ghost links. Built once for the Contested Zones section head's star
    // column; RefreshHangarStatus repaints every part from here on.
    private UIElement BuildHangarStatusLine()
    {
        var line = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            // Owner ruling (live run 2026-07-27): inset from the scroller edge; the line was
            // nearly touching the scrollbar.
            Margin = new Thickness(0, 0, 12, 0),
        };

        var dots = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        for (var i = 0; i < _hangarDots.Length; i++)
        {
            var dot = new Ellipse
            {
                Width = HangarDotSize, Height = HangarDotSize,
                Margin = new Thickness(i == 0 ? 0 : HangarDotGap, 0, 0, 0),
            };
            _hangarDots[i] = dot;
            dots.Children.Add(dot);
        }
        line.Children.Add(dots);

        _hangarPhaseText = new TextBlock
        {
            FontFamily = Hud.Font("TechFont"), FontSize = 11, FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(HangarItemGap, 0, 0, 0),
        };
        line.Children.Add(_hangarPhaseText);

        // Owner ruling (live run 2026-07-27): say what the number counts down TO ("closes in" /
        // "opens in"); a bare countdown reads ambiguously against the 185m full-cycle mental model.
        _hangarVerbText = new TextBlock
        {
            FontSize = 10.5, Foreground = Hud.Br("FgDimBrush"),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(HangarItemGap, 0, 0, 0),
        };
        line.Children.Add(_hangarVerbText);

        _hangarCountdownText = new TextBlock
        {
            FontFamily = Hud.Font("MonoFont"), FontSize = 11, Foreground = Hud.Br("FgBrush"),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(HangarItemGap, 0, 0, 0),
        };
        line.Children.Add(_hangarCountdownText);

        _hangarTailText = new TextBlock
        {
            Text = "still active", FontSize = 10, Foreground = Hud.Br("FgDimBrush"),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(HangarItemGap, 0, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        line.Children.Add(_hangarTailText);

        var reanchor = new TextBlock
        {
            Text = "Re-anchor", FontFamily = Hud.Font("UiFont"), FontSize = 10.5, Foreground = Hud.Br("AccentBrush"),
            Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(HangarItemGap, 0, 0, 0),
        };
        reanchor.MouseEnter += (_, _) => reanchor.TextDecorations = TextDecorations.Underline;
        reanchor.MouseLeave += (_, _) => reanchor.TextDecorations = null;
        reanchor.MouseLeftButtonDown += (_, _) => OnReanchorClicked();
        line.Children.Add(reanchor);

        var reset = new TextBlock
        {
            Text = "Reset", FontFamily = Hud.Font("UiFont"), FontSize = 10.5, Foreground = Hud.Br("FgDimBrush"),
            Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(HangarItemGap, 0, 0, 0), Visibility = Visibility.Collapsed,
        };
        reset.MouseEnter += (_, _) => reset.TextDecorations = TextDecorations.Underline;
        reset.MouseLeave += (_, _) => reset.TextDecorations = null;
        reset.MouseLeftButtonDown += (_, _) => OnResetClicked();
        line.Children.Add(reset);
        _hangarResetLink = reset;

        _hangarLine = line;
        return line;
    }

    // Next-opens readout under the head, right-aligned to echo the status line above it (one
    // line). Returns FrameworkElement (not just UIElement) so it can join _cascade like every
    // other visible section element (issue #26 review, minor finding).
    private FrameworkElement BuildHangarNextOpensLine()
    {
        _hangarNextOpensText = new TextBlock
        {
            FontFamily = Hud.Font("MonoFont"), FontSize = 10.5, Foreground = Hud.Br("FgDimBrush"),
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 4, 12, 0),
        };
        return _hangarNextOpensText;
    }

    // Recomputes the cycle from the current clock (and any Settings override) and repaints every
    // part of the status line in one pass. Called on initial build, every timer tick, and right
    // after a Re-anchor confirm or Reset so the line never waits for the next tick.
    private void RefreshHangarStatus()
    {
        var overrideUtc = App.Settings.Current.ExecHangarAnchorOverrideUtc;
        var snapshot = ExecHangarCycle.At(DateTime.UtcNow, overrideUtc);
        ApplyHangarSnapshot(snapshot, overrideUtc);
    }

    private void ApplyHangarSnapshot(ExecHangarSnapshot s, DateTime? overrideUtc)
    {
        var litBrush = s.IsOpen ? Hud.Br("AccentBrush") : Hud.Br("CyanBrush");
        var unlitBrush = Hud.Br("Bg3Brush");
        var unlitStroke = Hud.Br("NavBorderBrush");
        for (var i = 0; i < _hangarDots.Length; i++)
        {
            var lit = i < s.GreensLit;
            _hangarDots[i].Fill = lit ? litBrush : unlitBrush;
            _hangarDots[i].Stroke = lit ? null : unlitStroke;
            _hangarDots[i].StrokeThickness = 1;
        }

        _hangarPhaseText!.Text = s.IsOpen ? "OPEN" : "CLOSED";
        _hangarPhaseText!.Foreground = s.IsOpen ? Hud.Br("AccentBrush") : Hud.Br("FgDimBrush");

        _hangarVerbText!.Text = s.IsOpen ? "closes in" : "opens in";
        _hangarCountdownText!.Text = ExecHangarCycle.FormatCountdown(s.TimeToTransition);
        _hangarTailText!.Visibility = s.IsFinalActiveTail ? Visibility.Visible : Visibility.Collapsed;
        _hangarResetLink!.Visibility = overrideUtc.HasValue ? Visibility.Visible : Visibility.Collapsed;

        _hangarNextOpensText!.Text =
            "Next open: " + string.Join(", ", s.UpcomingOpensUtc.Select(FormatClockTime));

        var calibrationLine = overrideUtc.HasValue
            ? $"Custom anchor set {FormatAnchorLocal(overrideUtc.Value)}"
            : $"Cycle calibration: {ExecHangarCycle.CalibrationLabel}";
        _hangarLine!.ToolTip =
            calibrationLine + "\n" +
            "Uses your PC clock. Keep Windows time set to automatic.\n" +
            "If in-game lights look off with time remaining, trust the timer; the lights catch up at the next state change.";
    }

    // Time format follows Settings > Appearance > 24-hour clock - the same selection MainWindow's
    // top-bar clock reads (App.Settings.Current.Clock24Hour ? "HH:mm:ss" : "h:mm:ss tt") - trimmed
    // to minutes for this compact readout ("Next open: 19:42, 22:47, 01:52").
    private static string FormatClockTime(DateTime utc)
        => utc.ToLocalTime().ToString(App.Settings.Current.Clock24Hour ? "HH:mm" : "h:mm tt");

    // The stored override round-trips through JSON settings and can come back Local or
    // Unspecified (same caveat as ExecHangarCycle.At); normalize the same way before converting.
    private static string FormatAnchorLocal(DateTime storedUtc)
    {
        var utc = storedUtc.Kind == DateTimeKind.Local
            ? storedUtc.ToUniversalTime()
            : DateTime.SpecifyKind(storedUtc, DateTimeKind.Utc);
        return $"{utc.ToLocalTime():d MMM yyyy} {FormatClockTime(utc)}";
    }

    // Called from IsVisibleChanged every time the Guides page becomes visible (first visit and
    // every return trip - GuidesPage is a lazy singleton that is never removed from the visual
    // tree, so this is the only reliable "page entry" signal; Loaded fires once, ever). Recomputes
    // immediately so a stale value never shows while the timer was stopped, logs the once-per-entry
    // line, then starts the timer if it is not already running.
    private void HangarPageEntered()
    {
        if (_hangarLine == null) return;   // defensive: only wired once BuildSection has run
        RefreshHangarStatus();

        var overrideUtc = App.Settings.Current.ExecHangarAnchorOverrideUtc;
        var s = ExecHangarCycle.At(DateTime.UtcNow, overrideUtc);
        Logger.Info($"[UI] Exec hangar timer: {(s.IsOpen ? "OPEN" : "CLOSED")}, " +
            $"{ExecHangarCycle.FormatCountdown(s.TimeToTransition)} to {(s.IsOpen ? "close" : "open")} " +
            $"(anchor: {(overrideUtc.HasValue ? "custom" : "built-in " + ExecHangarCycle.CalibrationLabel)})");

        if (_hangarTimer != null) return;   // already running - never stack a second timer
        _hangarTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _hangarTimer.Tick += (_, _) => RefreshHangarStatus();
        _hangarTimer.Start();
    }

    // Called from IsVisibleChanged every time the Guides page is hidden (dock switch away). Stops
    // the timer so it never ticks for a page that is not on screen - this is the fix for the
    // Critical finding: Loaded/Unloaded never fires for this lazy-singleton page, so Unloaded-based
    // stopping never ran past the very first visit.
    private void HangarPageLeft()
    {
        _hangarTimer?.Stop();
        _hangarTimer = null;
    }

    private void OnResetClicked()
    {
        App.Settings.Current.ExecHangarAnchorOverrideUtc = null;
        App.Settings.Save();
        RefreshHangarStatus();
        Logger.Info("[UI] Exec hangar anchor reset to built-in");
    }

    private void OnReanchorClicked()
    {
        if (!ShowReanchorConfirm(Window.GetWindow(this))) return;
        var anchor = DateTime.UtcNow;   // stored UTC, never local time

        // One log line carrying everything a maintainer needs to recalibrate the built-in anchor
        // from a user's diagnostic snapshot: the observed open instant, which anchor was active
        // before, what that prior calibration predicted at the click (makes the drift visible),
        // the app version, and the shard id (its numeric chunk is the game server build the
        // community calibrations are keyed to). Captured BEFORE the override is replaced.
        var priorOverride = App.Settings.Current.ExecHangarAnchorOverrideUtc;
        var prior = ExecHangarCycle.At(anchor, priorOverride);
        var priorAnchorDesc = priorOverride.HasValue
            ? $"custom {priorOverride.Value:yyyy-MM-dd'T'HH:mm:ss.fff'Z'}"
            : "built-in " + ExecHangarCycle.CalibrationLabel;
        var shard = App.Shards.Current?.ShardId
            ?? App.Shards.All.FirstOrDefault()?.ShardId
            ?? "unknown";

        App.Settings.Current.ExecHangarAnchorOverrideUtc = anchor;
        App.Settings.Save();
        RefreshHangarStatus();
        Logger.Info($"[UI] Exec hangar re-anchored by user: {anchor:yyyy-MM-dd'T'HH:mm:ss.fff'Z'} UTC "
            + $"(app {AppInfo.Version}; prior anchor: {priorAnchorDesc}; prior prediction at click: "
            + $"{(prior.IsOpen ? "OPEN" : "CLOSED")}, {ExecHangarCycle.FormatCountdown(prior.TimeToTransition)} "
            + $"to {(prior.IsOpen ? "close" : "open")}; shard: {shard})");
    }

    // Compact themed confirm dialog. Chrome (themed Background/Foreground, ResizeMode, sizing)
    // is modeled on NetworkPage's small prompt/choice dialogs; DialogMotion.Attach and
    // UiScaleService.ApplyToDialog are added below to match the app's near-universal dialog
    // idiom (every dedicated *Dialog.cs, e.g. WorkOrderEditorDialog's identical
    // SizeToContent.Height sizing) - mouse only, still no default/cancel key bindings.
    private static bool ShowReanchorConfirm(Window? owner)
    {
        var win = new Window
        {
            Title = "RE-ANCHOR HANGAR CYCLE",
            Width = 420, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            Owner = owner,
            Background = Hud.Br("BgBrush"), Foreground = Hud.Br("FgBrush"),
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
        };

        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock
        {
            Text = "Set the cycle from this moment. Click confirm only at the instant the hangar opens.",
            FontFamily = Hud.Font("UiFont"), FontSize = 12.5, Foreground = Hud.Br("FgBrush"),
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Your re-anchor time is written to the app log. Please send a diagnostic snapshot "
                 + "(Settings > Diagnostics) to the project so the built-in calibration can be updated for everyone.",
            FontFamily = Hud.Font("UiFont"), FontSize = 11, Foreground = Hud.Br("FgDimBrush"),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0),
        });

        var cancel = new Button
        {
            Content = "Cancel", Style = (Style)Application.Current.FindResource("NexusButton"),
            Padding = new Thickness(16, 7, 16, 7), Cursor = Cursors.Hand,
        };
        var confirm = new Button
        {
            Content = "Confirm", Style = (Style)Application.Current.FindResource("AccentButton"),
            Padding = new Thickness(16, 7, 16, 7), Margin = new Thickness(8, 0, 0, 0), Cursor = Cursors.Hand,
        };
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };
        row.Children.Add(cancel);
        row.Children.Add(confirm);
        panel.Children.Add(row);

        win.Content = panel;
        confirm.Click += (_, _) => win.DialogResult = true;
        cancel.Click += (_, _) => win.DialogResult = false;
        DialogMotion.Attach(win);
        UiScaleService.ApplyToDialog(win, panel);   // App scale (issue #20)
        return win.ShowDialog() == true;
    }

    // -- entrance --------------------------------------------------------------------

    // Section heads, then cards, then the credits footer, on one continuous index.
    private void PlayCascade()
    {
        if (Motion.Reduced)
        {
            foreach (var fe in _cascade)
            {
                fe.BeginAnimation(OpacityProperty, null);
                fe.Opacity = 1;
                var t = Slide(fe);
                t.BeginAnimation(TranslateTransform.YProperty, null);
                t.Y = 0;
            }
            return;
        }

        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        for (int i = 0; i < _cascade.Count; i++)
        {
            var fe = _cascade[i];
            var slide = Slide(fe);
            fe.Opacity = 0;
            slide.Y = RisePx;
            var delay = TimeSpan.FromMilliseconds(i * CascadeStepMs);
            var dur = TimeSpan.FromMilliseconds(CascadeMs);
            fe.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, dur) { BeginTime = delay, EasingFunction = ease });
            slide.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(RisePx, 0, dur) { BeginTime = delay, EasingFunction = ease });
        }
    }

    // Cards already carry a TranslateTransform for the hover lift; reuse it so the entrance and
    // the lift never fight over RenderTransform.
    private static TranslateTransform Slide(FrameworkElement fe)
    {
        if (fe.RenderTransform is TranslateTransform t) return t;
        var created = new TranslateTransform();
        fe.RenderTransform = created;
        return created;
    }
}
