using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using NexusApp.Models;
using NexusApp.Services;
using NexusApp.ViewModels;

namespace NexusApp.Views;

/// <summary>
/// Operations dashboard - the command-center landing page, rebuilt on the shared
/// MOBIGLAS HUD primitives (chamfered panels, reticle hero, glowing status chips,
/// state progress bars). Aggregates live state from the existing services and is
/// rebuilt fresh on every visit. Read-only; the navigate callback drills in.
/// </summary>
public sealed class CommandPage : UserControl
{
    private readonly Action<string> _navigate;
    private readonly MainViewModel _vm;
    private readonly StackPanel _root = new() { Margin = new Thickness(24, 22, 26, 40) };

    private Grid? _kpiRow;

    /// <summary>The KPI card row, for the welcome tour's Operations step to ring.</summary>
    public FrameworkElement? KpiRowTarget => _kpiRow;

    // ── Auto-relaunch notice strip (render-crash recovery) ──
    // Session-scoped state: this page is one persistent instance, and both tab-opens and live data
    // ticks rebuild via Refresh(), so these fields keep one consistent strip across all rebuilds.
    // The strip shows only on a render-relaunch start (App.RelaunchedThisSession) until dismissed.
    private FrameworkElement? _relaunchStrip;      // current strip element, for the one-time entrance
    private bool _relaunchDismissed;               // user dismissed the strip this session
    private bool _relaunchStripLogged;             // "shown" logged once, not on every rebuild
    private bool _relaunchEntrancePlayed;          // one-time fade-rise played this session

    // ── Auto-update notice strips (consent, live update, updated-to) ──
    // Same session-scoped rebuild contract as the relaunch strip above: Refresh() rebuilds every
    // strip from scratch (on tab-open, on live data ticks, and on UpdateService.Changed), so the
    // dismiss/logged flags are what keep one consistent strip across all rebuilds.
    private bool _updateDismissed;                 // user dismissed the update strip this session
    private bool _updateStripLogged;               // "shown" logged once, not on every rebuild
    private bool _postUpdateDismissed;             // user dismissed the updated-to strip this session
    private bool _postUpdateStripLogged;           // "shown" logged once, not on every rebuild
    private FrameworkElement? _updateStrip;        // current update strip element

    // ── Operations entrance (tab-open only; never on data ticks) ──
    // Fires once per tab-open visit (MainWindow's SetActivePage calls PlayEntrance after
    // InitCommandPage/Refresh, and calls ResetEntrance whenever the page is not the active
    // one, so the next tab-open replays it). Refresh() itself never triggers this - the
    // dashboard rebuilds its content statically on every live data tick while the tab stays
    // open, and only PlayEntrance layers the cascade/count-up/sparkline reveal on top.
    private bool _entrancePlayed;

    // Number Runs/TextBlocks captured fresh on each Refresh(), so PlayEntrance can retrigger
    // their count-up. TextBlock targets (no separately-styled unit alongside the number) go
    // through the real CountUp.To; Run targets (Refinery Queue "active", Cargo In Transit
    // "SCU" - a second, differently-styled Run sits next to the number) go through the local
    // CountUpRun mirror below, since CountUp.cs only supports a whole TextBlock target.
    private readonly List<(DependencyObject El, double To, string Suffix)> _kpiCountTargets = new();

    private Polyline? _sparklinePoly;

    // Full-page overlay layer above the dashboard's ScrollViewer; hosts the install confirmation.
    private readonly Grid _modalHost;

    private Brush Br(string k) => (Brush)Application.Current.FindResource(k);
    private FontFamily Ui => (FontFamily)Application.Current.FindResource("UiFont");
    private FontFamily Disp => (FontFamily)Application.Current.FindResource("DisplayFont");
    private FontFamily Mono => (FontFamily)Application.Current.FindResource("MonoFont");

    public CommandPage(Action<string> navigate, MainViewModel vm)
    {
        _navigate = navigate;
        _vm = vm;
        // The scrolling dashboard and a full-page modal layer share one host grid, so the
        // install confirmation can scrim the whole page instead of scrolling with it.
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _root };
        _modalHost = new Grid { Visibility = Visibility.Collapsed };
        var host = new Grid();
        host.Children.Add(scroll);
        host.Children.Add(_modalHost);
        Content = host;
        // Keep the dashboard live (shard card + KPIs) when the shard changes while Operations is open.
        if (App.Shards != null) App.Shards.Changed += () => Dispatcher.Invoke(Refresh);
        // Update state changes arrive on a worker thread; marshal like the shard feed does.
        if (App.Update != null) App.Update.Changed += () => Dispatcher.Invoke(Refresh);
    }

    public void Refresh()
    {
        _root.Children.Clear();
        _root.Children.Add(HeaderRow());
        _relaunchStrip = RelaunchStrip();
        if (_relaunchStrip != null) _root.Children.Add(_relaunchStrip);
        var postUpdate = PostUpdateStrip();
        if (postUpdate != null) _root.Children.Add(postUpdate);
        var consent = ConsentStrip();
        if (consent != null) _root.Children.Add(consent);
        _updateStrip = UpdateStrip();
        if (_updateStrip != null) _root.Children.Add(_updateStrip);
        _root.Children.Add(KpiRow());
        _root.Children.Add(Panels());
    }

    /// <summary>
    /// Operations entrance: KPI cascade (fade+rise, 200ms/40ms stagger/12px, cap 5) + a
    /// count-up retrigger on the 5 KPI numbers + the Last Scan sparkline drawing itself in.
    /// Called by MainWindow's SetActivePage right after InitCommandPage/Refresh, ONLY on tab
    /// open - never on the live data ticks that also call Refresh() while the tab stays open.
    /// The _entrancePlayed flag (reset via ResetEntrance whenever this page is not the active
    /// one) makes this idempotent for the current visit even if called more than once.
    /// </summary>
    public void PlayEntrance()
    {
        if (_entrancePlayed) return;
        _entrancePlayed = true;
        if (Motion.Reduced) return;   // values/sparkline already render at their final state - only the reveal is skipped

        // The relaunch strip rides the same CascadeIn fade-rise as the KPI cards, gated on
        // Motion.Reduced by the early return above. Once per session, not on every tab-open.
        if (_relaunchStrip != null && !_relaunchEntrancePlayed)
        {
            _relaunchEntrancePlayed = true;
            CascadeInSingle(_relaunchStrip);
        }

        if (_kpiRow != null) CascadeIn(_kpiRow.Children, maxAnimated: 5);

        foreach (var (el, to, suffix) in _kpiCountTargets)
        {
            if (el is Run run) CountUpRun(run, to);
            else if (el is TextBlock tb) { CountUp.SetSuffix(tb, suffix); CountUp.SetTo(tb, to); }
        }

        AnimateSparkline();
    }

    /// <summary>Clears the entrance-played flag so the next tab-open plays it again.</summary>
    public void ResetEntrance() => _entrancePlayed = false;

    // Fade + rise the first few children in sequence (200ms/40ms stagger/12px rise, QuadraticEase
    // EaseOut) - a local copy of MainWindow's CascadeIn idiom (private there; copied here rather
    // than exposed, per the motion-pass plan).
    private static void CascadeIn(UIElementCollection children, int maxAnimated)
    {
        int n = Math.Min(children.Count, maxAnimated);
        for (int i = 0; i < n; i++)
        {
            if (children[i] is not FrameworkElement fe) continue;
            var slide = new TranslateTransform(0, 12);
            fe.RenderTransform = slide;
            fe.Opacity = 0;
            var delay = TimeSpan.FromMilliseconds(i * 40);
            var dur = TimeSpan.FromMilliseconds(200);
            var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
            var fade = new DoubleAnimation(0, 1, dur) { BeginTime = delay, EasingFunction = ease };
            var rise = new DoubleAnimation(12, 0, dur) { BeginTime = delay, EasingFunction = ease };
            fe.BeginAnimation(UIElement.OpacityProperty, fade);
            slide.BeginAnimation(TranslateTransform.YProperty, rise);
        }
    }

    // One-element version of CascadeIn (fade + 12px rise, 200ms, QuadraticEase EaseOut) - the
    // motion the mock's relaunch strip uses for its one-time entrance.
    private static void CascadeInSingle(FrameworkElement fe)
    {
        var slide = new TranslateTransform(0, 12);
        fe.RenderTransform = slide;
        fe.Opacity = 0;
        var dur = TimeSpan.FromMilliseconds(200);
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        fe.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, dur) { EasingFunction = ease });
        slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(12, 0, dur) { EasingFunction = ease });
    }

    // Local mirror of CountUp.cs's 0 -> to roll-up (Motion.CountUpMs, Motion.Settle), targeting
    // a Run instead of a whole TextBlock. Used where the number Run sits beside a second,
    // differently-styled unit Run (Refinery Queue "active", Cargo In Transit "SCU") - CountUp.cs
    // only supports a TextBlock target, which would overwrite that unit Run's own styling.
    private static readonly DependencyProperty RunCountProperty = DependencyProperty.RegisterAttached(
        "RunCount", typeof(double), typeof(CommandPage), new PropertyMetadata(0.0, OnRunCountChanged));

    private static void OnRunCountChanged(DependencyObject o, DependencyPropertyChangedEventArgs e)
    {
        if (o is Run run) run.Text = ((double)e.NewValue).ToString("N0");
    }

    private static void CountUpRun(Run run, double to)
    {
        run.SetValue(RunCountProperty, 0.0);
        run.Text = "0";
        var anim = new DoubleAnimation(0, to, new Duration(TimeSpan.FromMilliseconds(Motion.CountUpMs))) { EasingFunction = Motion.Settle };
        run.BeginAnimation(RunCountProperty, anim);
    }

    // Sparkline draw-on: StrokeDashOffset animates full-length -> 0 (600ms, Motion.SlideOut),
    // then the dash is cleared so subsequent live-data rebuilds (fresh Polyline instances) just
    // render plainly - copies the donut draw-in idiom at NetworkPage.cs (dash units are multiples
    // of StrokeThickness, so the raw pixel length is divided by it, same as the donut's radius/stroke).
    private void AnimateSparkline()
    {
        var poly = _sparklinePoly;
        if (poly == null || poly.Points.Count < 2) return;
        double lenPx = PolylineLength(poly.Points);
        if (lenPx <= 0) return;
        double dashLen = lenPx / poly.StrokeThickness;

        poly.StrokeDashArray = new DoubleCollection { dashLen };
        poly.StrokeDashOffset = dashLen;
        var anim = new DoubleAnimation(dashLen, 0, TimeSpan.FromMilliseconds(600)) { EasingFunction = Motion.SlideOut };
        anim.Completed += (_, _) =>
        {
            poly.BeginAnimation(Shape.StrokeDashOffsetProperty, null);
            poly.StrokeDashArray = null;
        };
        poly.BeginAnimation(Shape.StrokeDashOffsetProperty, anim);
    }

    private static double PolylineLength(PointCollection points)
    {
        double len = 0;
        for (int i = 1; i < points.Count; i++)
            len += (points[i] - points[i - 1]).Length;
        return len;
    }

    // ── header: glow-dash eyebrow + title + subtitle, with an ambient radar sweep accent ──
    private UIElement HeaderRow()
    {
        var radar = Hud.AmbientGlyph(Hud.Ambient.StatusBoard, 46);
        radar.VerticalAlignment = VerticalAlignment.Center;
        return Hud.Header("COMMAND", "Operations", "Everything live, in one glance. Drill into any module from the rail.", radar);
    }

    // ── auto-relaunch notice strip: amber alert shown on a render-relaunch start ──
    // Sits between the header and the KPI row, only when this session was auto-relaunched by
    // CrashGuard (App.RelaunchedThisSession) and not yet dismissed. Reuses the NetworkRisk
    // amber-alert idiom: tinted amber bg (0x14 amber), amber-line border, chamfered panel, amber
    // icon + caps eyebrow. Returns null when it should not show, so Refresh() simply omits it.
    private FrameworkElement? RelaunchStrip()
    {
        if (!App.RelaunchedThisSession || _relaunchDismissed) return null;

        if (!_relaunchStripLogged)
        {
            _relaunchStripLogged = true;
            Logger.Info("[UI] auto-relaunch notice shown on Operations");
        }

        return NoticeStrip("AUTOMATIC RESTART",
            "Nexus restarted itself after Windows reported a display error. Your work was not " +
            "affected. If this keeps happening, enable CPU rendering in Settings > Diagnostics.",
            Array.Empty<Button>(), DismissRelaunchStrip);
    }

    // Shared chrome for the amber notice strips (relaunch, consent, update, updated-to):
    // icon box + eyebrow + wrapping body + optional action buttons + optional dismiss.
    // Geometry and colors are the approved relaunch-strip values; only the words differ.
    private FrameworkElement NoticeStrip(string eyebrow, string bodyText, IEnumerable<Button> actions, Action? onDismiss)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // amber icon
        grid.ColumnDefinitions.Add(new ColumnDefinition());                             // eyebrow + message
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // action + dismiss buttons

        // Glyph in a 30x30 amber-line box (mock .rs-icon: 1px amber-line border, radius 4,
        // bg rgba(255,178,62,0.06)); the 17px viewbox glyph is centered inside it.
        var icon = new Border
        {
            Width = 30, Height = 30, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(4, 0, 14, 0),
            BorderBrush = Br("AccentStrongBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Color.FromArgb(0x0F, 0xFF, 0xB2, 0x3E)),
            Child = new Viewbox
            {
                Width = 17, Height = 17,
                Child = new Path
                {
                    Data = Geometry.Parse("M21,12 A9,9 0 1 1 18,5.3 M21,4 L21,9 L16,9"),
                    Stroke = Br("AccentBrush"), StrokeThickness = 1.7, Fill = Brushes.Transparent,
                    Width = 24, Height = 24, Stretch = Stretch.Uniform,
                },
            },
        };
        Grid.SetColumn(icon, 0); grid.Children.Add(icon);

        var body = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        body.Children.Add(new TextBlock
        {
            Text = eyebrow, FontFamily = Disp, FontSize = 10, FontWeight = FontWeights.Bold,
            Foreground = Br("AccentBrush"), Margin = new Thickness(0, 0, 0, 5),
        });
        body.Children.Add(new TextBlock
        {
            Text = bodyText, FontFamily = Ui, FontSize = 12.5, Foreground = Br("FgBrush"),
            TextWrapping = TextWrapping.Wrap, MaxWidth = 640, HorizontalAlignment = HorizontalAlignment.Left,
        });
        Grid.SetColumn(body, 1); grid.Children.Add(body);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        foreach (var b in actions) { b.Margin = new Thickness(14, 0, 0, 0); buttons.Children.Add(b); }
        if (onDismiss != null)
        {
            var dismiss = StripButton("Dismiss");
            dismiss.Click += (_, _) => onDismiss();
            buttons.Children.Add(dismiss);
        }
        Grid.SetColumn(buttons, 2); grid.Children.Add(buttons);

        var panel = Hud.Panel(grid, chamfer: 12, padding: new Thickness(14, 12, 14, 12),
                              bg: new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xB2, 0x3E)),
                              border: Br("AccentStrongBrush"));
        // Left amber edge accent (mock .relaunch-strip::before: 2px bar, 10px top/bottom inset, radius 2, amber glow).
        panel.Children.Add(new Border
        {
            Width = 2, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Stretch,
            Margin = new Thickness(0, 10, 0, 10), CornerRadius = new CornerRadius(2),
            Background = Br("AccentBrush"), IsHitTestVisible = false,
            Effect = new DropShadowEffect { Color = ((SolidColorBrush)Br("AccentBrush")).Color, BlurRadius = 8, ShadowDepth = 0, Opacity = 0.8 },
        });
        panel.Margin = new Thickness(0, 0, 0, 16);
        return panel;
    }

    // The strips' shared action button (the approved relaunch-strip dismiss geometry).
    private static Button StripButton(string label) => new()
    {
        Content = label,
        Style = (Style)Application.Current.FindResource("NexusButton"),
        Padding = new Thickness(13, 6, 13, 6),
        Margin = new Thickness(14, 0, 0, 0),
        VerticalAlignment = VerticalAlignment.Top,
    };

    // Session-scoped dismiss: collapse the strip for the rest of this session. Setting the flag first
    // means any interleaving Refresh() already omits the strip; the short fade (unless motion is
    // reduced) just tidies the outgoing element before the rebuild drops it from layout.
    private void DismissRelaunchStrip()
    {
        if (_relaunchDismissed) return;
        _relaunchDismissed = true;
        Logger.Info("[UI] auto-relaunch notice dismissed");

        var strip = _relaunchStrip;
        if (strip == null || Motion.Reduced) { Refresh(); return; }
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(Motion.HoverMs)) { EasingFunction = Motion.SlideOut };
        fade.Completed += (_, _) => Refresh();
        strip.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    // ── auto-update notice strips ──
    // All three share the relaunch strip's amber chrome (NoticeStrip) and take every word from
    // UpdateNotice. Each returns null when it should not show, so Refresh() simply omits it.

    // One-time opt-in for update checks: shows until answered, never in the demo profile.
    private FrameworkElement? ConsentStrip()
    {
        if (!UpdateNotice.ShouldShowConsentStrip(App.Settings.Current.UpdateCheckEnabled, AppPaths.IsDemoProfile)) return null;

        var enable = StripButton(UpdateNotice.ConsentEnable);
        enable.Click += (_, _) =>
        {
            App.Settings.Current.UpdateCheckEnabled = true;
            App.Settings.Save();
            Logger.Info("[UPDATE] consent: enabled");
            Refresh();
            _ = App.Update.CheckAsync(manual: false);
        };
        var decline = StripButton(UpdateNotice.ConsentDecline);
        decline.Click += (_, _) =>
        {
            App.Settings.Current.UpdateCheckEnabled = false;
            App.Settings.Save();
            Logger.Info("[UPDATE] consent: declined");
            Refresh();
        };
        return NoticeStrip(UpdateNotice.ConsentEyebrow, UpdateNotice.ConsentBody, new[] { enable, decline }, onDismiss: null);
    }

    // One-session announcement after a successful update.
    private FrameworkElement? PostUpdateStrip()
    {
        if (_postUpdateDismissed) return null;
        // The whole update feature is inert in the demo profile (spec section 7): the demo
        // root keeps its own LastSeenVersion across app upgrades, and this strip must never
        // appear in the public-screenshot profile.
        if (AppPaths.IsDemoProfile) return null;
        if (!UpdateNotice.ShouldShowPostUpdateStrip(App.PreviousSessionVersion, AppInfo.Version)) return null;

        if (!_postUpdateStripLogged)
        {
            _postUpdateStripLogged = true;
            Logger.Info("[UI] post-update notice shown on Operations");
        }
        return NoticeStrip(UpdateNotice.PostUpdateEyebrow, UpdateNotice.PostUpdateBody(AppInfo.Version),
            Array.Empty<Button>(), () =>
            {
                _postUpdateDismissed = true;
                Logger.Info("[UI] post-update notice dismissed");
                Refresh();
            });
    }

    // The live update strip: body and actions follow the service state. Dismiss is
    // session-scoped; the Settings > Diagnostics rows remain the persistent surface.
    private FrameworkElement? UpdateStrip()
    {
        var svc = App.Update;
        if (svc == null || _updateDismissed || svc.Available is null) return null;
        if (svc.State is not (UpdateState.UpdateAvailable or UpdateState.Downloading or UpdateState.Verifying or UpdateState.ReadyToInstall)) return null;

        if (!_updateStripLogged)
        {
            _updateStripLogged = true;
            Logger.Info("[UI] update notice shown on Operations");
        }

        var v = svc.Available.Version;
        var actions = new List<Button>();
        string bodyText;
        switch (svc.State)
        {
            case UpdateState.Downloading:
                bodyText = UpdateNotice.DownloadingBody(v, svc.DownloadedBytes, svc.TotalBytes);
                break;
            case UpdateState.Verifying:
                bodyText = UpdateNotice.VerifyingBody(v);
                break;
            case UpdateState.ReadyToInstall when AppInfo.Distribution == "Installer":
                bodyText = UpdateNotice.ReadyBodyInstaller(v);
                var install = StripButton("Install update");
                install.Click += (_, _) => ShowInstallConfirm(v);
                actions.Add(install);
                break;
            case UpdateState.ReadyToInstall:
                bodyText = UpdateNotice.ReadyBodyPortable(v);
                var open = StripButton("Open folder");
                open.Click += (_, _) => App.Update.OpenDownloadFolder();
                actions.Add(open);
                break;
            default:
                bodyText = UpdateNotice.UpdateBody(AppInfo.Version, v);
                if (svc.Available.AssetFor(AppInfo.Distribution) != null)
                {
                    var download = StripButton("Download");
                    download.Click += async (_, _) => { await App.Update.DownloadAsync(); ShowVerifyFailedIfNeeded(); };
                    actions.Add(download);
                }
                break;
        }
        return NoticeStrip(UpdateNotice.UpdateEyebrow, bodyText, actions,
            () => { _updateDismissed = true; Logger.Info("[UI] update notice dismissed"); Refresh(); });
    }

    // Themed in-page confirmation (SettingsPage danger-modal pattern, accent chrome): the
    // click that hands control to the installer deserves more weight than a MessageBox.
    private void ShowInstallConfirm(Version v)
    {
        Logger.Info("[UI] install update: confirm shown");
        _modalHost.Children.Clear();

        var scrim = new Border { Background = new SolidColorBrush(Color.FromArgb(0x9E, 0x03, 0x05, 0x08)) };
        scrim.MouseLeftButtonDown += (_, _) => CloseInstallConfirm(cancelled: true);
        _modalHost.Children.Add(scrim);

        var body = new StackPanel();
        body.Children.Add(new TextBlock
        {
            Text = UpdateNotice.InstallConfirmTitle(v), FontFamily = Hud.Font("TechFont"),
            FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Br("FgBrush"),
            TextWrapping = TextWrapping.Wrap,
        });
        body.Children.Add(new TextBlock
        {
            Text = UpdateNotice.InstallConfirmBody, FontSize = 12.5, Foreground = Br("FgDimBrush"),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0),
        });
        var actionRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var cancel = StripButton("Cancel");
        cancel.Margin = new Thickness(0);
        cancel.Click += (_, _) => CloseInstallConfirm(cancelled: true);
        var confirm = StripButton("Install update");
        // Visual weight over Cancel: the two buttons are otherwise identical chrome, and the
        // consequential one should read as the primary action.
        confirm.Foreground = Br("AccentBrush");
        confirm.FontWeight = FontWeights.SemiBold;
        confirm.Click += (_, _) =>
        {
            Logger.Info("[UI] install update: confirmed");
            CloseInstallConfirm(cancelled: false);
            if (App.Update.LaunchInstaller())
                Application.Current.Shutdown();
            else
                ShowVerifyFailedIfNeeded();
        };
        actionRow.Children.Add(cancel);
        actionRow.Children.Add(confirm);
        body.Children.Add(actionRow);

        var panel = Hud.Panel(body, chamfer: 14, bg: Br("Bg2NavBrush"), border: Br("AccentStrongBrush"),
                              padding: new Thickness(22, 20, 22, 22));
        panel.MaxWidth = 470;
        panel.VerticalAlignment = VerticalAlignment.Center;
        panel.HorizontalAlignment = HorizontalAlignment.Center;
        panel.MouseLeftButtonDown += (_, e) => e.Handled = true;
        _modalHost.Children.Add(panel);
        _modalHost.Visibility = Visibility.Visible;
    }

    private void CloseInstallConfirm(bool cancelled)
    {
        if (cancelled) Logger.Info("[UI] install update: cancelled");
        _modalHost.Visibility = Visibility.Collapsed;
        _modalHost.Children.Clear();
    }

    // Spec-mandated warning when a download or install-time hash check refused the file.
    // Keyed on LastFailureWasVerification so ordinary network failures stay quiet here.
    private void ShowVerifyFailedIfNeeded()
    {
        if (App.Update?.LastFailureWasVerification != true) return;
        var owner = Window.GetWindow(this);
        if (owner != null)
            MessageBox.Show(owner, UpdateNotice.VerifyFailedBody, UpdateNotice.VerifyFailedTitle,
                MessageBoxButton.OK, MessageBoxImage.Warning);
        else
            MessageBox.Show(UpdateNotice.VerifyFailedBody, UpdateNotice.VerifyFailedTitle,
                MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    // ── 4 KPI cards: Last scan (hero, reticle) · Refinery queue · Cargo · Session ──
    private UIElement KpiRow()
    {
        var orders = App.Data.GetWorkOrders();
        int activeOrders = orders.Count(o => o.Status != WorkOrderStatus.Complete);
        int ready = orders.Count(o => o.Status == WorkOrderStatus.ReadyToCollect);
        var hauls = App.Hauls.ActiveHauls;
        int scu = hauls.Sum(h => h.Legs.Sum(l => l.TargetScu));
        int session = App.GameLog.Count;

        // Network coverage: share of the blueprint catalog owned by you or any network member.
        var catalog = App.Data.GetAllBlueprints();
        int bpTotal = catalog.Count;
        var ownerCounts = App.Network.OwnerCounts();
        int covered = catalog.Count(b => (ownerCounts.TryGetValue(b.Name, out var c) && c > 0) || App.Settings.IsBlueprintOwned(b.Name));
        int covPct = bpTotal > 0 ? (int)System.Math.Round(100.0 * covered / bpTotal) : 0;

        // Rebuilt fresh every Refresh() (tab-open AND live data ticks) - reset the count-up
        // targets so PlayEntrance only ever sees the current visit's fresh elements.
        _kpiCountTargets.Clear();

        var grid = new Grid { Margin = new Thickness(0, 0, 0, 16), Height = 132 };
        for (int i = 0; i < 5; i++) grid.ColumnDefinitions.Add(new ColumnDefinition());

        var cards = new UIElement[]
        {
            LastScanCard(),
            Kpi(IconRefinery(), "REFINERY QUEUE", activeOrders.ToString("N0"), "active", ready > 0 ? $"{ready} ready to collect" : "none ready", ready > 0, "FgBrush", activeOrders),
            Kpi(IconCargo(), "CARGO IN TRANSIT", scu.ToString("N0"), "SCU", $"{hauls.Count} active haul(s)", false, "CyanBrush", scu),
            Kpi(IconBlueprint(), "SESSION BLUEPRINTS", session.ToString("N0"), "", "Auto-tracked from Game.log", false, "CyanBrush", session),
            Kpi(IconNetwork(), "NETWORK COVERAGE", covPct + "%", "", $"{covered} of {bpTotal} owned", false, "CyanBrush", covPct, "%"),
        };
        for (int i = 0; i < cards.Length; i++)
        {
            ((FrameworkElement)cards[i]).Margin = new Thickness(i == 0 ? 0 : 7, 0, i == cards.Length - 1 ? 0 : 7, 0);
            Grid.SetColumn(cards[i], i);
            grid.Children.Add(cards[i]);
        }
        _kpiRow = grid;
        return grid;
    }

    private UIElement LastScanCard()
    {
        var last = _vm.ScanHistory.FirstOrDefault();
        var sp = new StackPanel();
        sp.Children.Add(KpiLabel(IconScan(), "LAST SCAN · RS"));
        if (last != null)
        {
            var val = new TextBlock { Text = last.Rs.ToString("N0"), FontFamily = Disp, FontSize = 34, FontWeight = FontWeights.Bold, Foreground = Br("GoldBrush"), Margin = new Thickness(0, 6, 0, 0) };
            val.Effect = new DropShadowEffect { Color = ((SolidColorBrush)Br("GoldBrush")).Color, BlurRadius = 16, ShadowDepth = 0, Opacity = 0.35 };
            sp.Children.Add(val);
            _kpiCountTargets.Add((val, last.Rs, ""));
            var match = last.Match == MatchKind.Exact ? "exact" : last.Match == MatchKind.Close ? "close" : "no match";
            var foot = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            foot.Children.Add(new Ellipse { Width = 7, Height = 7, Fill = Br("AccentBrush"), Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center });
            foot.Children.Add(new TextBlock { Text = $"{last.TopResource} · {match}", FontFamily = Ui, FontSize = 11, Foreground = Br("FgDimBrush"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            sp.Children.Add(foot);
        }
        else
        {
            sp.Children.Add(new TextBlock { Text = "-", FontFamily = Disp, FontSize = 34, FontWeight = FontWeights.Bold, Foreground = Br("FgDimBrush"), Margin = new Thickness(0, 6, 0, 0) });
            sp.Children.Add(new TextBlock { Text = "No scans yet", FontFamily = Ui, FontSize = 11, Foreground = Br("FgDimBrush"), Margin = new Thickness(0, 6, 0, 0) });
        }

        var inner = new Grid();
        inner.Children.Add(sp);
        inner.Children.Add(Sparkline());

        var panel = Hud.Panel(inner, chamfer: 12, glow: false, border: Br("AccentStrongBrush"), padding: new Thickness(16, 14, 16, 14));
        Hud.AttachReticle(panel, 18);
        return panel;
    }

    private UIElement Sparkline()
    {
        var vals = _vm.ScanHistory.Take(7).Select(e => (double)e.Rs).Reverse().ToList();
        if (vals.Count < 2) { _sparklinePoly = null; return new Grid(); }
        double min = vals.Min(), max = vals.Max(), range = max - min < 1 ? 1 : max - min;
        const double w = 86, h = 26;
        var poly = new Polyline { Stroke = Br("CyanBrush"), StrokeThickness = 1.6, StrokeLineJoin = PenLineJoin.Round, VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Right, Width = w, Height = h };
        poly.Effect = new DropShadowEffect { Color = ((SolidColorBrush)Br("CyanBrush")).Color, BlurRadius = 6, ShadowDepth = 0, Opacity = 0.5 };
        for (int i = 0; i < vals.Count; i++)
        {
            double x = w * i / (vals.Count - 1);
            double y = h - h * (vals[i] - min) / range;
            poly.Points.Add(new Point(x, y));
        }
        // Drawn fully visible by default (data ticks just render it plainly) - PlayEntrance
        // is what dashes it and animates the reveal, tab-open only.
        _sparklinePoly = poly;
        return poly;
    }

    private UIElement Kpi(UIElement icon, string key, string val, string unit, string foot, bool accent, string valueBrush, double countTo, string countSuffix = "")
    {
        var sp = new StackPanel();
        sp.Children.Add(KpiLabel(icon, key));
        var value = new TextBlock { FontFamily = Disp, FontSize = 34, FontWeight = FontWeights.Bold, Foreground = Br(valueBrush), Margin = new Thickness(0, 6, 0, 0) };
        if (valueBrush == "CyanBrush")
            value.Effect = new DropShadowEffect { Color = ((SolidColorBrush)Br("CyanBrush")).Color, BlurRadius = 14, ShadowDepth = 0, Opacity = 0.3 };
        var numberRun = new Run(val);
        value.Inlines.Add(numberRun);
        if (!string.IsNullOrEmpty(unit))
        {
            value.Inlines.Add(new Run("  " + unit) { FontFamily = Ui, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Br("FgDimBrush") });
            // A second, differently-styled Run sits next to the number - CountUp.cs only
            // supports a whole TextBlock target, so drive just the number Run instead.
            _kpiCountTargets.Add((numberRun, countTo, ""));
        }
        else
        {
            _kpiCountTargets.Add((value, countTo, countSuffix));
        }
        sp.Children.Add(value);
        var footEl = new TextBlock { FontFamily = Ui, FontSize = 11, Foreground = Br("FgDimBrush"), Margin = new Thickness(0, 6, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        if (accent)
        {
            footEl.Inlines.Add(new Run("READY ") { Foreground = Br("GoldBrush"), FontWeight = FontWeights.Bold });
            footEl.Inlines.Add(new Run(foot.Replace("ready to collect", "to collect")));
        }
        else footEl.Text = foot;
        sp.Children.Add(footEl);

        return Hud.Panel(sp, chamfer: 12, brackets: false, border: accent ? Br("AccentStrongBrush") : Br("NavBorderBrush"),
                         padding: new Thickness(16, 14, 16, 14));
    }

    private UIElement KpiLabel(UIElement icon, string text)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(icon);
        row.Children.Add(new TextBlock { Text = text, FontFamily = Ui, FontSize = 10, FontWeight = FontWeights.Bold, Foreground = Br("FgDimBrush"), VerticalAlignment = VerticalAlignment.Center });
        return row;
    }

    // small cyan line icons for the KPI labels
    private UIElement Icon(string data) => new Viewbox
    {
        Width = 13, Height = 13, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center,
        Child = new Path { Data = Geometry.Parse(data), Stroke = Br("CyanBrush"), StrokeThickness = 1.4, Fill = Brushes.Transparent, Width = 16, Height = 16, Stretch = Stretch.Uniform },
    };
    private UIElement IconScan() => Icon("M7,1 A6,6 0 1,0 7,13 A6,6 0 1,0 7,1 M11,11 L15,15");
    private UIElement IconRefinery() => Icon("M2,15 L2,6 L7,9 L7,6 L12,9 L12,15 Z");
    private UIElement IconCargo() => Icon("M2,5 L14,5 L14,14 L2,14 Z M2,8 L14,8");
    private UIElement IconBlueprint() => Icon("M2,2 L14,2 L14,14 L2,14 Z M8,2 L8,14 M2,8 L14,8");
    private UIElement IconNetwork() => Icon("M4,5 L12,5 M4,5 L8,13 M12,5 L8,13");

    // ── refinery queue table (left) + active hauls / network risk (right, stacked) ──
    private UIElement Panels()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.45, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var left = Hud.Panel(RefineryQueue(), chamfer: 14, padding: new Thickness(18));
        left.Margin = new Thickness(0, 0, 8, 0);
        Grid.SetColumn(left, 0); grid.Children.Add(left);

        var right = new StackPanel { Margin = new Thickness(8, 0, 0, 0) };
        right.Children.Add(Hud.Panel(ActiveHauls(), chamfer: 14, padding: new Thickness(18)));
        var risk = NetworkRisk();
        if (risk != null) { risk.Margin = new Thickness(0, 12, 0, 0); right.Children.Add(risk); }
        var shardCard = ShardCard();
        shardCard.Margin = new Thickness(0, 12, 0, 0);
        right.Children.Add(shardCard);
        Grid.SetColumn(right, 1); grid.Children.Add(right);
        return grid;
    }

    private UIElement RefineryQueue()
    {
        var sp = new StackPanel();
        sp.Children.Add(PanelHead("REFINERY QUEUE", "Open tracker", "workorders"));
        var active = App.Data.GetWorkOrders().Where(o => o.Status != WorkOrderStatus.Complete).ToList();
        if (active.Count == 0) { sp.Children.Add(Empty("No active work orders.")); return sp; }

        sp.Children.Add(TableRow(Th("ORDER"), Th("STATION"), Th("STATUS"), Th("REMAINING", right: true), header: true));
        foreach (var o in active)
        {
            var order = new TextBlock
            {
                Text = !string.IsNullOrWhiteSpace(o.Label) ? o.Label : (!string.IsNullOrWhiteSpace(o.Resources) ? o.Resources : "Work order"),
                FontFamily = Ui, FontSize = 12.5, Foreground = Br("FgBrush"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var station = new TextBlock { Text = !string.IsNullOrWhiteSpace(o.Refinery) ? o.Refinery : o.Location, FontFamily = Ui, FontSize = 12, Foreground = Br("FgDimBrush"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 8, 0) };
            var chipHolder = new ContentControl { Content = Hud.StatusChip(o.Status), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
            var remTxt = !string.IsNullOrEmpty(o.TimerRemainingShort) ? o.TimerRemainingShort : (o.Status == WorkOrderStatus.ReadyToCollect ? "ready" : "-");
            var rem = new TextBlock { Text = remTxt, FontFamily = Mono, FontSize = 11.5, Foreground = o.Status == WorkOrderStatus.ReadyToCollect ? Br("GoldBrush") : Br("FgDimBrush"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(TableRow(order, station, chipHolder, rem));
        }
        return sp;
    }

    private static readonly GridLength[] _cols =
        { new(1, GridUnitType.Star), new(120), new(104), new(86) };

    private UIElement TableRow(UIElement c0, UIElement c1, UIElement c2, UIElement c3, bool header = false)
    {
        var g = new Grid();
        foreach (var w in _cols) g.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
        var cells = new[] { c0, c1, c2, c3 };
        for (int i = 0; i < 4; i++) { Grid.SetColumn(cells[i], i); g.Children.Add(cells[i]); }
        return new Border
        {
            BorderBrush = Br("NavBorderBrush"), BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(0, header ? 0 : 11, 0, header ? 9 : 11), Child = g,
        };
    }

    private TextBlock Th(string t, bool right = false) => new()
    {
        Text = t, FontFamily = Ui, FontSize = 9, FontWeight = FontWeights.Bold, Foreground = Br("FgDimBrush"),
        HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left,
    };

    private UIElement ActiveHauls()
    {
        var sp = new StackPanel();
        sp.Children.Add(PanelHead("ACTIVE HAULS", "Open hauling", "hauling"));
        var hauls = App.Hauls.ActiveHauls;
        if (hauls.Count == 0)
            sp.Children.Add(Empty("No active hauls."));
        else
            foreach (var h in hauls)
            {
                var drops = h.Legs.Where(l => l.Role == HaulRole.Dropoff).ToList();
                int total = drops.Sum(l => l.TargetScu);
                int done = drops.Where(l => l.Completed).Sum(l => l.TargetScu);
                double frac = total > 0 ? (double)done / total : 0;

                var top = new Grid { Margin = new Thickness(0, 8, 0, 5) };
                top.ColumnDefinitions.Add(new ColumnDefinition());
                top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var title = !string.IsNullOrWhiteSpace(h.RouteTitle) ? h.RouteTitle : (!string.IsNullOrWhiteSpace(h.Topology) ? h.Topology : "Haul");
                var t = new TextBlock { Text = title, FontFamily = Ui, FontSize = 12.5, Foreground = Br("FgBrush"), TextTrimming = TextTrimming.CharacterEllipsis };
                Grid.SetColumn(t, 0); top.Children.Add(t);
                var scu = new TextBlock { Text = $"{done:N0} / {total:N0} SCU", FontFamily = Mono, FontSize = 11, Foreground = Br("CyanBrush"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(scu, 1); top.Children.Add(scu);
                sp.Children.Add(top);
                sp.Children.Add(Hud.StateBar(frac, frac >= 1 ? Hud.BarState.Green : Hud.BarState.Cyan));
            }
        return sp;
    }

    // Network risk as its own standalone amber alert card (matches the mock).
    private FrameworkElement? NetworkRisk()
    {
        if (App.Network.MemberCount == 0) return null;
        var counts = App.Network.OwnerCounts();
        int single = 0;
        foreach (var b in App.Data.GetAllBlueprints())
        {
            int o = (counts.TryGetValue(b.Name, out var c) ? c : 0) + (App.Settings.IsBlueprintOwned(b.Name) ? 1 : 0);
            if (o == 1) single++;
        }

        var sp = new StackPanel();
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        head.Children.Add(new Viewbox { Width = 15, Height = 15, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center, Child = new Path { Data = Geometry.Parse("M8,1 L15,14 L1,14 Z M8,6 L8,10 M8,12 L8,12.5"), Stroke = Br("AccentBrush"), StrokeThickness = 1.4, Fill = Brushes.Transparent, Width = 16, Height = 16, Stretch = Stretch.Uniform } });
        head.Children.Add(new TextBlock { Text = "NETWORK RISK", FontFamily = Ui, FontSize = 10, FontWeight = FontWeights.Bold, Foreground = Br("AccentBrush"), VerticalAlignment = VerticalAlignment.Center });
        sp.Children.Add(head);

        sp.Children.Add(new TextBlock { Text = single == 0 ? "No single-owner blueprints." : $"{single} blueprint(s) have only one owner.", FontFamily = Ui, FontSize = 12.5, Foreground = Br("FgBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        var review = new TextBlock { Text = "Review  →", FontFamily = Ui, FontSize = 11.5, Foreground = Br("CyanBrush"), Cursor = System.Windows.Input.Cursors.Hand };
        review.MouseLeftButtonUp += (_, _) => _navigate("network");
        sp.Children.Add(review);

        return Hud.Panel(sp, chamfer: 12, padding: new Thickness(14, 12, 14, 12),
                         bg: new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xB2, 0x3E)), border: Br("AccentStrongBrush"));
    }

    // Server / shard card: the current shard (region + instance + raw id) and up to 3 recent shards,
    // mirroring the overlay's STATS shard panel. Reads App.Shards (Current / Recent); shard metadata only.
    private FrameworkElement ShardCard()
    {
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = "SERVER / SHARD", FontFamily = Ui, FontSize = 11, FontWeight = FontWeights.Bold, Foreground = Br("FgBrush"), Margin = new Thickness(0, 0, 0, 10) });

        var current = App.Shards.Current;
        if (current != null)
        {
            sp.Children.Add(new TextBlock { Text = "CURRENT", FontFamily = Ui, FontSize = 9, FontWeight = FontWeights.Bold, Foreground = Br("FgDimBrush"), Margin = new Thickness(0, 0, 0, 3) });
            var card = new StackPanel();
            card.Children.Add(new TextBlock { Text = $"{current.Region}  -  Shard {current.Instance}", FontFamily = Ui, FontSize = 13, Foreground = Br("CyanBrush"), TextTrimming = TextTrimming.CharacterEllipsis });
            card.Children.Add(new TextBlock { Text = current.ShardId, FontFamily = Mono, FontSize = 10, Foreground = Br("FgDimBrush"), Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
            sp.Children.Add(new Border { Child = card, Background = Br("Bg2NavBrush"), BorderBrush = Br("NavBorderBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(13, 9, 13, 9) });
        }
        else
        {
            sp.Children.Add(new TextBlock { Text = "Not on a shard.", FontSize = 12, Foreground = Br("FgDimBrush") });
        }

        var recent = App.Shards.Recent;
        if (recent.Count > 0)
        {
            sp.Children.Add(new TextBlock { Text = "RECENT", FontFamily = Ui, FontSize = 9, FontWeight = FontWeights.Bold, Foreground = Br("FgDimBrush"), Margin = new Thickness(0, 12, 0, 4) });
            foreach (var s in recent)
                sp.Children.Add(new TextBlock { Text = $"{Ago(s.JoinedAt)}   {s.Region} - {s.Instance}", FontFamily = Mono, FontSize = 10.5, Foreground = Br("FgDimBrush"), Margin = new Thickness(4, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
        }

        return Hud.Panel(sp, chamfer: 14, padding: new Thickness(18));
    }

    // Compact relative-time label for a UTC instant: "just now" / "Nm ago" / "Nh ago" / "Nd ago".
    private static string Ago(DateTime utcWhen)
    {
        var span = DateTime.UtcNow - utcWhen;
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        if (span.TotalMinutes < 1) return "just now";
        if (span.TotalHours   < 1) return $"{(int)span.TotalMinutes}m ago";
        if (span.TotalDays    < 1) return $"{(int)span.TotalHours}h ago";
        return $"{(int)span.TotalDays}d ago";
    }

    // ── small helpers ──
    private UIElement PanelHead(string title, string link, string nav)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        g.Children.Add(new TextBlock { Text = title, FontFamily = Ui, FontSize = 11, FontWeight = FontWeights.Bold, Foreground = Br("FgBrush") });
        var a = new TextBlock { Text = link + "  →", FontFamily = Ui, FontSize = 11, Foreground = Br("AccentBrush"), HorizontalAlignment = HorizontalAlignment.Right, Cursor = System.Windows.Input.Cursors.Hand };
        a.MouseLeftButtonUp += (_, _) => _navigate(nav);
        g.Children.Add(a);
        return g;
    }

    private UIElement Empty(string t) => new TextBlock { Text = t, FontFamily = Ui, FontSize = 12, Foreground = Br("FgDimBrush"), Margin = new Thickness(0, 4, 0, 0) };
}
