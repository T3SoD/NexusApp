using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using NexusApp.Services;
using NexusApp.ViewModels;

namespace NexusApp.Views;

/// <summary>
/// Operations dashboard - the command-center landing page, rebuilt on the shared
/// MOBIGLAS HUD primitives (chamfered panels, glowing status chips, state progress
/// bars). Aggregates live state from the existing services and is rebuilt fresh on
/// every visit. Read-only; the navigate callback drills in.
///
/// <para>This file owns the page frame: the header, the notice strips, the entrance
/// animation and the live ticker. The body it hosts is the system-view layout in
/// CommandPage.SystemView.cs.</para>
/// </summary>
public sealed partial class CommandPage : UserControl
{
    private readonly Action<string> _navigate;
    private readonly MainViewModel _vm;
    private readonly StackPanel _root = new() { Margin = new Thickness(24, 22, 26, 40) };

    /// <summary>The job card strip, for the welcome tour's Operations step to ring. Named for the
    /// tour step it serves rather than the cards it points at, so the anchor survives the next
    /// layout change the way it did not survive this one.</summary>
    public FrameworkElement? KpiRowTarget => _jobStrip;

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
    private bool _swapFailedDismissed;             // user dismissed the swap-failed strip this session
    private bool _swapFailedStripLogged;           // "shown" logged once, not on every rebuild

    // ── Custom Game.log folder notice (issue #28) ──
    // Same session-scoped rebuild contract as the strips above. Dismiss persists the exact path
    // to AppSettings.CustomChannelNoticePath instead of a session-only flag, so a DIFFERENT
    // custom path notifies again but this one never re-nags across restarts either.
    private bool _customChannelStripLogged;        // "shown" logged once, not on every rebuild

    // ── Operations entrance (tab-open only; never on data ticks) ──
    // Fires once per tab-open visit (MainWindow's SetActivePage calls PlayEntrance after
    // InitCommandPage/Refresh, and calls ResetEntrance whenever the page is not the active
    // one, so the next tab-open replays it). Refresh() itself never triggers this - the
    // dashboard rebuilds its content statically on every live data tick while the tab stays
    // open, and only PlayEntrance layers the cascade/count-up/sparkline reveal on top.
    private bool _entrancePlayed;

    // Number Runs/TextBlocks captured fresh on each Refresh(), so PlayEntrance can retrigger
    // their count-up. TextBlock targets (no separately-styled unit alongside the number) go
    // through the real CountUp.To; Run targets (a second, differently-styled unit Run sits next
    // to the number) go through the local CountUpRun mirror below, since CountUp.cs only supports
    // a whole TextBlock target.
    private readonly List<(DependencyObject El, double To, string Suffix)> _kpiCountTargets = new();

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
        // The shard tracker is pumped by the shared Game.log feed's DispatcherTimer, so it already
        // raises on the UI thread and needs no marshaling.
        if (App.Shards != null) App.Shards.Changed += Refresh;
        // Update state changes arrive on a worker thread, so those DO need marshaling.
        if (App.Update != null) App.Update.Changed += () => Dispatcher.Invoke(Refresh);
        // Channel switches (LIVE <-> PTU/EPTU/etc, and into/out of a custom folder, issue #28)
        // decide the custom-channel strip's visibility, so Operations needs its own trigger to
        // pick this up live - same UI-thread contract as the shard/update wiring above (the
        // watcher's DispatcherTimer already raises on the UI thread, per GameLogFeed.cs).
        App.GameLogFeed.ChannelChanged += _ => Refresh();

        // App review 2026-08-01: the page subtitle promises "Everything live, in one glance" and the
        // class comment above asserts the dashboard "rebuilds its content statically on every live
        // data tick", but the only triggers were the three subscriptions above plus tab activation.
        // Nothing listened for hauls or work orders, so accepting a contract in game did not move
        // CARGO IN TRANSIT or ACTIVE HAULS, and the refinery rows sat frozen. HaulTracker was
        // already raising the Changed event nobody had subscribed to. Unfinished wiring, not a
        // decision - the entrance cascade is separately gated behind _entrancePlayed, so a Refresh
        // on a data tick has always been the intended mechanism and never replays the animation.
        //
        // Guarded on IsVisible, unlike the three above, because hauls and work orders tick far more
        // often than a shard or channel change and rebuilding a hidden page's whole visual tree for
        // each one is pure waste. Nothing is missed: MainWindow's SetActivePage calls Refresh on
        // every activation, so a page that skipped updates while hidden catches up on open. Same
        // idiom as TradePage and MapPage's own permanent subscriptions.
        App.Hauls.Changed += () => Dispatcher.BeginInvoke(() => { if (IsVisible) Refresh(); });
        _vm.WorkOrders.CollectionChanged += (_, _) => Dispatcher.BeginInvoke(() => { if (IsVisible) Refresh(); });
        // The header subtitle now reports where the player was last seen, so a boundary crossing has
        // to repaint it. Same guard and same idiom MapPage uses for its own player marker.
        App.Locations.Changed += () => Dispatcher.BeginInvoke(() => { if (IsVisible) Refresh(); });
        // Session profit card (issue #39): kiosk settlements and the game going on or offline both
        // change what it says. Both signals already raise on the UI thread (the tracker coalesces
        // through the dispatcher, the feed's probe is DispatcherTimer-pumped); same guard-and-
        // catch-up idiom as the hauls subscription above.
        if (App.Profit != null) App.Profit.Changed += () => Dispatcher.BeginInvoke(() => { if (IsVisible) Refresh(); });
        if (App.Wallet != null) App.Wallet.Changed += () => Dispatcher.BeginInvoke(() => { if (IsVisible) Refresh(); });
        App.GameLogFeed.SessionLiveChanged += _ => Dispatcher.BeginInvoke(() => { if (IsVisible) Refresh(); });

        // REMAINING countdown (app review F11). Every other trigger on this page is a DATA change,
        // and a countdown has none - the number goes stale purely because time passed. So the
        // refinery rows sat frozen at whatever they said when the page was last rebuilt.
        //
        // IsVisible, not Loaded: Operations is a lazy singleton kept permanently in MainWindow's
        // tree, and page switches are pure Visibility toggling, so Loaded/Unloaded never fire for it
        // (the same trap GuidesPage documents for its own hangar line).
        IsVisibleChanged += (_, _) => { if (IsVisible) StartLiveTicker(); else _liveTicker?.Stop(); };
    }

    // The one-hertz clock behind the job strip's two countdown cards (auto load, exec hangar). The
    // cells themselves and the no-op guard live in CommandPage.SystemView.cs; only the timer is
    // here, because IsVisible owns its lifetime.
    //
    // IsVisible, not Loaded: Operations is a lazy singleton kept permanently in MainWindow's tree,
    // and page switches are pure Visibility toggling, so Loaded/Unloaded never fire for it (the
    // same trap GuidesPage documents for its own hangar line).
    private DispatcherTimer? _liveTicker;

    private DispatcherTimer MakeLiveTicker()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => TickLiveCells();
        return timer;
    }

    // The header and the strip stack, so Refresh can rebuild those in place. _root itself is
    // assembled once and never cleared: the body below it hosts the system view's WebView2, which
    // is a native window, and detaching it from the tree on every live data tick would reparent
    // that window several times a minute while the player is flying.
    private readonly ContentControl _headerHost = new();
    private readonly StackPanel _stripHost = new();

    public void Refresh()
    {
        if (_root.Children.Count == 0)
        {
            _root.Children.Add(_headerHost);
            _root.Children.Add(_stripHost);
            _root.Children.Add(SystemViewFrame());
        }

        _headerHost.Content = HeaderRow();

        _stripHost.Children.Clear();
        _relaunchStrip = RelaunchStrip();
        if (_relaunchStrip != null) _stripHost.Children.Add(_relaunchStrip);
        var postUpdate = PostUpdateStrip();
        if (postUpdate != null) _stripHost.Children.Add(postUpdate);
        var swapFailed = SwapFailedStrip();
        if (swapFailed != null) _stripHost.Children.Add(swapFailed);
        var customChannel = CustomChannelStrip();
        if (customChannel != null) _stripHost.Children.Add(customChannel);
        var consent = ConsentStrip();
        if (consent != null) _stripHost.Children.Add(consent);
        _updateStrip = UpdateStrip();
        if (_updateStrip != null) _stripHost.Children.Add(_updateStrip);

        // Rebuilt fresh every Refresh() (tab-open AND live data ticks) - reset the count-up
        // targets so PlayEntrance only ever sees the current visit's fresh elements.
        _kpiCountTargets.Clear();
        FillSystemViewBody();
    }

    /// <summary>
    /// Operations entrance: job card cascade (fade+rise, 200ms/40ms stagger/12px) plus a count-up
    /// retrigger on the coverage percentage. Called by MainWindow's SetActivePage right after
    /// InitCommandPage/Refresh, ONLY on tab open - never on the live data ticks that also call
    /// Refresh() while the tab stays open. The _entrancePlayed flag (reset via ResetEntrance
    /// whenever this page is not the active one) makes this idempotent for the current visit even
    /// if called more than once.
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

        if (_jobStrip != null) CascadeIn(_jobStrip.Children, maxAnimated: 3);

        foreach (var (el, to, suffix) in _kpiCountTargets)
        {
            if (el is Run run) CountUpRun(run, to);
            else if (el is TextBlock tb) { CountUp.SetSuffix(tb, suffix); CountUp.SetTo(tb, to); }
        }
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

    // ── header: glow-dash eyebrow + title + subtitle, with an ambient radar sweep accent ──
    private UIElement HeaderRow()
    {
        var radar = Hud.AmbientGlyph(Hud.Ambient.StatusBoard, 46);
        radar.VerticalAlignment = VerticalAlignment.Center;
        // The subtitle now says where you are when the app knows. App review: this page had ZERO
        // references to App.Locations or any geometry catalog, so the landing page could not answer
        // the most basic live question the app already had the answer to. Possible now that
        // App.Player exists; before the refactor, position was locked inside two other pages.
        return Hud.Header("COMMAND", "Operations", HeaderSubtitle(), radar);
    }

    /// <summary>The subtitle, with the player's location folded in when it is known. Pure string
    /// assembly so the rule is testable: silence when there is no session, which is the normal state
    /// with the game closed and must not read as an error or a placeholder.</summary>
    internal static string OperationsSubtitle(string? placeLabel, string? system)
    {
        const string Base = "Everything live, in one glance. Drill into any module from the rail.";
        if (string.IsNullOrWhiteSpace(placeLabel)) return Base;

        // The system is only appended when it is known AND not already implied by the label - the
        // planet-named jurisdictions ("microTech") would otherwise read "microTech, Stanton" which
        // is fine, but "Stanton, Stanton" would not be.
        var where = !string.IsNullOrWhiteSpace(system)
                    && !string.Equals(system, placeLabel, StringComparison.OrdinalIgnoreCase)
            ? $"{placeLabel}, {system}"
            : placeLabel;
        return $"Last seen at {where}. " + Base;
    }

    private static string HeaderSubtitle()
        => OperationsSubtitle(App.Player?.Label, App.Player?.System);

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
    // The chrome itself lives on Hud (Hud.NoticeStrip / Hud.StripButton) because the market
    // data consent host on the decoder, codex and tracker pages renders the same strip;
    // these two keep the local names this page's strip builders already call.
    private static FrameworkElement NoticeStrip(string eyebrow, string bodyText, IEnumerable<Button> actions, Action? onDismiss)
        => Hud.NoticeStrip(eyebrow, bodyText, actions, onDismiss);

    // The strips' shared action button (the approved relaunch-strip dismiss geometry).
    private static Button StripButton(string label) => Hud.StripButton(label);

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

    // One-session notice after startup recovery restored the previous version (a swap
    // crashed mid-flight last session). Try again re-enters the normal flow with a fresh
    // check; Update manually routes the next ReadyToInstall to the guided manual flow.
    private FrameworkElement? SwapFailedStrip()
    {
        if (_swapFailedDismissed || AppPaths.IsDemoProfile) return null;
        if (App.SwapFailedAttemptedVersion is not { } attempted) return null;
        if (!_swapFailedStripLogged)
        {
            _swapFailedStripLogged = true;
            Logger.Info("[UI] swap-failed notice shown on Operations");
        }
        var tryAgain = StripButton("Try again");
        tryAgain.Click += (_, _) =>
        {
            _swapFailedDismissed = true;
            Logger.Info("[UI] swap-failed notice: try again");
            _ = App.Update.CheckAsync(manual: true);
            Refresh();
        };
        var manual = StripButton("Update manually");
        manual.Click += (_, _) =>
        {
            _swapFailedDismissed = true;
            Logger.Info("[UI] swap-failed notice: update manually");
            App.Update.PreferManualUpdate = true;
            _ = App.Update.CheckAsync(manual: true);
            Refresh();
        };
        return NoticeStrip(UpdateNotice.UpdateEyebrow, UpdateNotice.SwapFailedBody(attempted, AppInfo.Version),
            new[] { tryAgain, manual }, () =>
            {
                _swapFailedDismissed = true;
                Logger.Info("[UI] swap-failed notice dismissed");
                Refresh();
            });
    }

    // One-time notice when the watched Game.log lives in a custom (unrecognized) folder:
    // blueprint recording is off by default there (issue #28). Dismiss remembers the exact
    // path, so a DIFFERENT custom path notifies again but this one never re-nags.
    private FrameworkElement? CustomChannelStrip()
    {
        if (AppPaths.IsDemoProfile) return null;
        if (!CustomChannelNotice.ShouldShow(App.GameLogFeed.ActiveChannel, App.GameLogFeed.Path,
                App.Settings.Current.CustomChannelNoticePath,
                App.Settings.Current.CustomChannelRecordsBlueprints)) return null;

        if (!_customChannelStripLogged)
        {
            _customChannelStripLogged = true;
            Logger.Info("[UI] custom-channel notice shown on Operations");
        }

        var open = StripButton("Open Settings");
        open.Click += (_, _) =>
        {
            Logger.Info("[UI] custom-channel notice: open settings");
            (Application.Current.MainWindow as MainWindow)?.OpenSettingsGameTab();
        };
        return NoticeStrip("GAME LOG",
            "Nexus is reading a Game.log in a custom folder. Blueprint recording is off by default " +
            "for non-standard installs. If this is your real LIVE install, allow it in Settings > Game.",
            new[] { open }, onDismiss: () =>
            {
                App.Settings.Current.CustomChannelNoticePath = App.GameLogFeed.Path;
                App.Settings.Save();
                Logger.Info("[UI] custom-channel notice dismissed");
                // The Settings GAME tab's pip mirrors this same notice state, and that page is built
                // once and kept, so it has to be told the notice was acknowledged here.
                (Application.Current.MainWindow as MainWindow)?.RefreshSettingsGameDot();
                Refresh();
            });
    }

    // The live update strip: body and actions follow the service state. Dismiss is
    // session-scoped; the Settings > Updates rows remain the persistent surface.
    private FrameworkElement? UpdateStrip()
    {
        var svc = App.Update;
        if (svc == null || _updateDismissed || svc.Available is null) return null;
        // Installing is admitted only for the portable flows (the installer sets Installing
        // right before shutdown and must not flash a strip); ManualHandoff holds the
        // instruction line until the user closes Nexus themselves.
        var portableBusy = svc.State == UpdateState.Installing && (svc.PortableApplyInProgress || svc.ManualUnpackInProgress);
        if (svc.State is not (UpdateState.UpdateAvailable or UpdateState.Downloading or UpdateState.Verifying
                or UpdateState.ReadyToInstall or UpdateState.ManualHandoff) && !portableBusy) return null;

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
            // A stuck rollback is not retryable in this session: the previous version is in
            // .old files and only the next start's recovery can put it back. Say that, and
            // offer no button that would fail.
            case UpdateState.ReadyToInstall when svc.LastPortableApplyFailure != null && svc.LastApplyLeftRestorePending:
                bodyText = UpdateNotice.RestorePendingBody;
                break;
            case UpdateState.ReadyToInstall when svc.LastPortableApplyFailure != null:
                bodyText = UpdateNotice.PrepareFailedBody;
                var retry = StripButton("Try again");
                // Retry whichever path failed: the self-swap when it is available, otherwise
                // the guided manual unpack (its failure sets the same note).
                retry.Click += (_, _) =>
                {
                    Logger.Info("[UI] portable update: try again");
                    if (App.Update.PortableSwapAvailable) _ = RunPortableApply(v);
                    else _ = App.Update.UnpackForManualAsync();
                };
                actions.Add(retry);
                break;
            case UpdateState.ReadyToInstall when svc.PortableSwapAvailable:
                bodyText = UpdateNotice.ReadyBodyPortable(v);
                var installPortable = StripButton("Install update");
                installPortable.Click += (_, _) => ShowInstallConfirm(v);
                actions.Add(installPortable);
                break;
            case UpdateState.ReadyToInstall:
                bodyText = UpdateNotice.ReadyBodyPortableManual(v);
                var manual = StripButton("Set up manual update");
                manual.Click += (_, _) => { Logger.Info("[UI] portable update: manual handoff chosen"); _ = App.Update.UnpackForManualAsync(); };
                actions.Add(manual);
                break;
            case UpdateState.Installing when svc.ManualUnpackInProgress:
                bodyText = UpdateNotice.UnpackingBody(v);
                break;
            case UpdateState.Installing:
                bodyText = UpdateNotice.PreparingBody(v);
                break;
            case UpdateState.ManualHandoff:
                bodyText = UpdateNotice.ManualHandoffBody(v);
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
        // No buttons, no dismiss while the swap is preparing or unpacking: the phase is
        // terminal and the strip is the progress line.
        Action? onDismiss = portableBusy ? null :
            () => { _updateDismissed = true; Logger.Info("[UI] update notice dismissed"); Refresh(); };
        return NoticeStrip(UpdateNotice.UpdateEyebrow, bodyText, actions, onDismiss);
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
            Text = AppInfo.Distribution == "Installer" ? UpdateNotice.InstallConfirmBody : UpdateNotice.InstallConfirmBodyPortable,
            FontSize = 12.5, Foreground = Br("FgDimBrush"),
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
            if (AppInfo.Distribution == "Installer")
            {
                if (App.Update.LaunchInstaller())
                    Application.Current.Shutdown();
                else
                    ShowVerifyFailedIfNeeded();
            }
            else
            {
                _ = RunPortableApply(v);
            }
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

    // Confirm already given: quiesce the UI (version-skew guard: nothing may lazily load
    // PenImc or WebView2 natives after files change), release WebView2's file handles, then
    // run the swap off-thread. On success the shutdown path relaunches; on failure the
    // window comes back alive and the strip explains via LastPortableApplyFailure.
    private async Task RunPortableApply(Version v)
    {
        var owner = Window.GetWindow(this);
        ShowInstallingOverlay(v);
        if (owner != null) owner.IsEnabled = false;
        (owner as MainWindow)?.ShutdownWebViewsForUpdate();
        var ok = await App.Update.ApplyPortableAsync();
        if (ok)
        {
            Application.Current.Shutdown();
            return;
        }
        if (owner != null) owner.IsEnabled = true;
        CloseInstallingOverlay();
    }

    // Non-dismissible by design: the scrim doubles as the interaction guard while files are
    // being flipped, and a one-second dark gap after it reads as an ordinary restart.
    private void ShowInstallingOverlay(Version v)
    {
        _modalHost.Children.Clear();
        _modalHost.Children.Add(new Border { Background = new SolidColorBrush(Color.FromArgb(0x9E, 0x03, 0x05, 0x08)) });
        var body = new StackPanel();
        body.Children.Add(new TextBlock
        {
            Text = UpdateNotice.PreparingBody(v), FontFamily = Hud.Font("TechFont"),
            FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Br("FgBrush"),
            TextWrapping = TextWrapping.Wrap,
        });
        var panel = Hud.Panel(body, chamfer: 14, bg: Br("Bg2NavBrush"), border: Br("AccentStrongBrush"),
                              padding: new Thickness(22, 20, 22, 22));
        panel.MaxWidth = 470;
        panel.VerticalAlignment = VerticalAlignment.Center;
        panel.HorizontalAlignment = HorizontalAlignment.Center;
        _modalHost.Children.Add(panel);
        _modalHost.Visibility = Visibility.Visible;
    }

    private void CloseInstallingOverlay()
    {
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

    // small cyan line icons for the panel labels
    private UIElement Icon(string data) => new Viewbox
    {
        Width = 13, Height = 13, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center,
        Child = new Path { Data = Geometry.Parse(data), Stroke = Br("CyanBrush"), StrokeThickness = 1.4, Fill = Brushes.Transparent, Width = 16, Height = 16, Stretch = Stretch.Uniform },
    };
    private UIElement IconRefinery() => Icon("M2,15 L2,6 L7,9 L7,6 L12,9 L12,15 Z");
    private UIElement IconCargo() => Icon("M2,5 L14,5 L14,14 L2,14 Z M2,8 L14,8");
    private UIElement IconNetwork() => Icon("M4,5 L12,5 M4,5 L8,13 M12,5 L8,13");

    // Compact relative-time label for a UTC instant: "just now" / "Nm ago" / "Nh ago" / "Nd ago".
    private static string Ago(DateTime utcWhen) => MarketNotice.FormatAge(DateTime.UtcNow - utcWhen);

    // ── small helpers ──
    private UIElement PanelHead(string title, string link, string nav)
    {
        // Star + auto columns so a long title trims instead of running under the right-docked
        // link (the ALL TIME COMMODITY TRADING PROFIT head was the first to collide).
        var g = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(new TextBlock { Text = title, FontFamily = Ui, FontSize = 11, FontWeight = FontWeights.Bold, Foreground = Br("FgBrush"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 10, 0) });
        var a = new TextBlock { Text = link + "  →", FontFamily = Ui, FontSize = 11, Foreground = Br("AccentBrush"), HorizontalAlignment = HorizontalAlignment.Right, Cursor = System.Windows.Input.Cursors.Hand };
        Grid.SetColumn(a, 1);
        a.MouseEnter += (_, _) => a.TextDecorations = TextDecorations.Underline;
        a.MouseLeave += (_, _) => a.TextDecorations = null;
        a.MouseLeftButtonUp += (_, _) => _navigate(nav);
        g.Children.Add(a);
        return g;
    }

    private UIElement Empty(string t) => new TextBlock { Text = t, FontFamily = Ui, FontSize = 12, Foreground = Br("FgDimBrush"), Margin = new Thickness(0, 4, 0, 0) };
}
