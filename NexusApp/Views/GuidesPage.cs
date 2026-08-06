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
using NexusApp.Services;
using NexusApp.Services.Map;

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
    /// <summary>A guide card's place strip was clicked (app review G7b). MainWindow switches to the
    /// MAP tab and focuses the object, mirroring MapPage.OpenGuideRequested in the other direction -
    /// the two features now know about each other both ways.</summary>
    public event Action<int>? ShowOnMapRequested;

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

    // Executive Hangar status line (issue #26; extracted into a shared control across GuidesPage
    // and the overlay GUIDES tab by the live-run scope expansion): the control owns its own
    // DispatcherTimer, started and stopped from the IsVisibleChanged handler below (GuidesPage is
    // a lazy singleton kept permanently in MainWindow's tree; page switches are pure Visibility
    // toggling, which never fires Loaded/Unloaded).
    private ExecHangarStatusLine? _hangarLine;

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
        // field comment on _hangarLine), so it drives the hangar control's start/stop on every
        // entry and exit, not just the first one.
        IsVisibleChanged += (_, _) => { if (IsVisible) _hangarLine?.Start(); else _hangarLine?.Stop(); };
    }

    /// <summary>Called by MainWindow every time the dock activates this page.</summary>
    public void Activate()
    {
        Logger.Info("[UI] guides page opened");
        if (_openGuide != null) CloseGuide(replayCascade: false);
        PlayCascade();
    }

    // -- guide open / close ----------------------------------------------------------

    /// <summary>Opens a guide by catalog id, called by MainWindow when the MAP tab's OPEN GUIDE
    /// button fires (Task 10). Duplicates OpenGuide's open path rather than calling it directly:
    /// that overload takes a click's DependencyObject for InteractionLog, which a programmatic
    /// jump has none of. An id that no longer resolves (a stale pin) is a silent no-op with its
    /// own log line, matching the map-tab callers' unresolved-id convention.</summary>
    internal void ShowGuideById(string id)
    {
        var guide = GuideCatalog.All.FirstOrDefault(g => g.Id == id);
        if (guide == null)
        {
            Logger.Info($"[UI] guide open miss: {id}");
            return;
        }

        _openGuide = guide;
        _listHost.Visibility = Visibility.Collapsed;
        _viewer.Visibility = Visibility.Visible;
        _viewer.Show(guide);
        Logger.Info($"[UI] guide opened: {id} (map)");
    }

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
        // (issue #26); every other category keeps the plain rule exactly as before. The control
        // contains both rows (status line + next-opens) and joins the cascade via the head below,
        // not separately.
        var isContestedZones = category == "Contested Zones";
        if (isContestedZones)
        {
            _hangarLine = new ExecHangarStatusLine(compact: false, surfaceName: "guides")
            {
                // Ruling from live run 2026-07-27: inset from the scroller edge; the line was
                // nearly touching the scrollbar.
                Margin = new Thickness(0, 0, 12, 0),
            };
            Grid.SetColumn(_hangarLine, 2);
            head.Children.Add(_hangarLine);
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

        // WHERE THIS GUIDE IS (app review G7b). Every guide is a map of a real place, and the MAP
        // tab has drawn those places as its GUIDES layer since it shipped - but the cards never
        // said where they were, so the two features knew about each other in one direction only.
        // Absent for the two Tactical Strike Groups guides, which document a formation rather than
        // a location: those cards keep exactly the shape they have today.
        if (GuidePlaces.Describe(App.Map, guide.Id, App.Player.Current) is { } where)
        {
            var place = new TextBlock
            {
                Text = $"◆  {where}", FontFamily = Hud.Font("UiFont"), FontSize = 10.5,
                Foreground = Hud.Br("FgDimBrush"), Margin = new Thickness(0, 4, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis, Cursor = Cursors.Hand,
                ToolTip = "Show this place on the Starmap.",
            };
            place.MouseEnter += (_, _) => place.Foreground = Hud.Br("AccentBrush");
            place.MouseLeave += (_, _) => place.Foreground = Hud.Br("FgDimBrush");
            place.MouseLeftButtonUp += (_, e) =>
            {
                // Never bubble: the whole card opens the guide viewer, and this one strip inside it
                // does something else. Same guard the planner row's PIN chip uses.
                e.Handled = true;
                if (GuidePlaces.Resolve(App.Map, guide.Id) is not { } obj) return;
                Logger.Info($"[UI] guides: show {obj.Name} on the map");
                ShowOnMapRequested?.Invoke(obj.Id);
            };
            meta.Children.Add(place);
        }

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
