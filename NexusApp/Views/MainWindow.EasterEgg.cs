using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NexusApp.Services;

namespace NexusApp.Views;

// Split from MainWindow.xaml.cs (app review, Task 13): Easter egg (app version badge).
public partial class MainWindow
{
    // ── Easter egg (app version badge) ───────────────────────────────────────
    private int _eggClicks;
    private bool _eggArmed;   // a payoff is pending; further clicks must not cancel it
    private bool _eggResetHooked;
    private int _eggHintGeneration;   // bumped on every show/hide so a stale fade-out cannot close a re-shown popup
    private System.Windows.Threading.DispatcherTimer? _eggTimer;
    private static readonly string[] _eggWarnings =
        ["I wouldn't do that...", "Don't.", "Great! Now you've pissed off CannonActual!"];

    // Transmission Intercept payoff geometry: the chamfered panel is the visible dialog, the
    // window carries a transparent gutter so the blur-22 panel glow is not clipped at its edge.
    private const double EggPanelWidth  = 380;
    private const double EggPanelHeight = 205;
    private const double EggGlowGutter  = 24;

    private void AppBadge_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        InteractionLog.Click("app version badge", (DependencyObject)sender);
        if (_eggArmed) return;

        _eggClicks++;
        EggHintLabel.Text = _eggWarnings[Math.Min(_eggClicks - 1, _eggWarnings.Length - 1)];
        FadeEggHint(true);
        AnimateEggBadge(_eggClicks);

        _eggTimer ??= CreateEggTimer();
        _eggTimer.Stop();

        if (_eggClicks >= 3)
        {
            _eggClicks = 0;
            _eggArmed = true;
            _eggTimer.Interval = TimeSpan.FromMilliseconds(900);
        }
        else
        {
            _eggTimer.Interval = TimeSpan.FromSeconds(2);
        }
        _eggTimer.Start();
    }

    // One reusable timer for both the hint timeout and the payoff delay: the tick reads the
    // current armed state, so re-arming never leaves a stale timer running.
    private System.Windows.Threading.DispatcherTimer CreateEggTimer()
    {
        var t = new System.Windows.Threading.DispatcherTimer();
        t.Tick += (s, _) =>
        {
            ((System.Windows.Threading.DispatcherTimer)s!).Stop();
            FadeEggHint(false);
            if (!_eggArmed) return;
            _eggArmed = false;
            ShowEggDialog();
        };
        return t;
    }

    // The hint fades in and out instead of snapping; Reduce Animations sets the opacity outright.
    // The hint lives in a Popup below the badge, so the fade drives the inner Border (a Popup's own
    // Opacity does not render) and the popup only closes once the fade-out has finished.
    private void FadeEggHint(bool show)
    {
        var to = show ? 1.0 : 0.0;
        var generation = ++_eggHintGeneration;   // a re-show invalidates any in-flight fade-out

        if (show) EggHintPopup.IsOpen = true;

        if (Motion.Reduced)
        {
            EggHintBox.BeginAnimation(UIElement.OpacityProperty, null);
            EggHintBox.Opacity = to;
            if (!show) EggHintPopup.IsOpen = false;
            return;
        }

        var fade = new System.Windows.Media.Animation.DoubleAnimation(EggHintBox.Opacity, to,
            TimeSpan.FromMilliseconds(Motion.PageFadeMs)) { EasingFunction = Motion.Settle };
        if (!show)
            fade.Completed += (_, _) =>
            {
                if (generation != _eggHintGeneration) return;   // a newer show/hide owns the popup now
                EggHintPopup.IsOpen = false;
            };
        EggHintBox.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    // A floating Popup sits above other applications, so an alt-tab away from Nexus must drop the
    // hint instantly instead of leaving it over the game.
    private void HideEggHintNow()
    {
        _eggHintGeneration++;
        EggHintBox.BeginAnimation(UIElement.OpacityProperty, null);
        EggHintBox.Opacity = 0;
        EggHintPopup.IsOpen = false;
    }

    private static System.Windows.Media.Brush EggBrush(string key)
        => (System.Windows.Media.Brush)Application.Current.FindResource(key);

    private static System.Windows.Media.Color EggColor(string key)
        => (System.Windows.Media.Color)Application.Current.FindResource(key);

    private static System.Windows.Media.Animation.EasingDoubleKeyFrame EggKey(double value, double ms)
        => new(value, System.Windows.Media.Animation.KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ms)), Motion.Settle);

    // The badge border has to be animatable, so the XAML gives it a local SolidColorBrush.
    // Frozen brushes cannot animate, so fall back to a live clone of AccentDim if that ever changes.
    private System.Windows.Media.SolidColorBrush EggBadgeBrush()
    {
        if (AppVersionBadge.BorderBrush is System.Windows.Media.SolidColorBrush b && !b.IsFrozen) return b;
        var live = new System.Windows.Media.SolidColorBrush(EggColor("AccentDimColor"));
        AppVersionBadge.BorderBrush = live;
        return live;
    }

    // Badge escalation: click 1 tilts, click 2 pulses the amber glow, click 3 shakes and holds a
    // danger border until the payoff closes. Reduce Animations keeps the state, drops the movement.
    private void AnimateEggBadge(int click)
    {
        if (!_eggResetHooked)
        {
            _eggResetHooked = true;
            Closing += (_, _) => ResetEggBadge();
            Deactivated += (_, _) => HideEggHintNow();
        }

        var brush = EggBadgeBrush();
        if (Motion.Reduced)
        {
            if (click >= 3)
                brush.BeginAnimation(System.Windows.Media.SolidColorBrush.ColorProperty,
                    new System.Windows.Media.Animation.ColorAnimation(EggColor("DangerColor"), TimeSpan.Zero));
            return;
        }

        if (click == 1)
        {
            var tilt = new System.Windows.Media.Animation.DoubleAnimationUsingKeyFrames
            { Duration = new Duration(TimeSpan.FromMilliseconds(240)) };
            tilt.KeyFrames.Add(EggKey(0, 0));
            tilt.KeyFrames.Add(EggKey(-2.5, 70));
            tilt.KeyFrames.Add(EggKey(2, 150));
            tilt.KeyFrames.Add(EggKey(0, 240));
            AppBadgeRotate.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, tilt);
        }
        else if (click == 2)
        {
            var pulse = new System.Windows.Media.Animation.DoubleAnimationUsingKeyFrames
            { Duration = new Duration(TimeSpan.FromMilliseconds(440)) };
            pulse.KeyFrames.Add(EggKey(0, 0));
            pulse.KeyFrames.Add(EggKey(0.6, 220));
            pulse.KeyFrames.Add(EggKey(0, 440));
            AppBadgeGlow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty, pulse);

            var flash = new System.Windows.Media.Animation.ColorAnimationUsingKeyFrames
            { Duration = new Duration(TimeSpan.FromMilliseconds(350)) };
            flash.KeyFrames.Add(EggColorKey("AccentDimColor", 0));
            flash.KeyFrames.Add(EggColorKey("AccentHoverColor", 175));
            flash.KeyFrames.Add(EggColorKey("AccentDimColor", 350));
            brush.BeginAnimation(System.Windows.Media.SolidColorBrush.ColorProperty, flash);
        }
        else
        {
            var shake = new System.Windows.Media.Animation.DoubleAnimationUsingKeyFrames
            { Duration = new Duration(TimeSpan.FromMilliseconds(260)) };
            shake.KeyFrames.Add(EggKey(0, 0));
            shake.KeyFrames.Add(EggKey(-4, 52));
            shake.KeyFrames.Add(EggKey(4, 104));
            shake.KeyFrames.Add(EggKey(-3, 156));
            shake.KeyFrames.Add(EggKey(3, 208));
            shake.KeyFrames.Add(EggKey(0, 260));
            AppBadgeShift.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, shake);
            brush.BeginAnimation(System.Windows.Media.SolidColorBrush.ColorProperty,
                new System.Windows.Media.Animation.ColorAnimation(EggColor("DangerColor"), TimeSpan.Zero));
        }
    }

    private static System.Windows.Media.Animation.EasingColorKeyFrame EggColorKey(string key, double ms)
        => new(EggColor(key), System.Windows.Media.Animation.KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ms)), Motion.Settle);

    // Drops every badge animation back to its XAML base: angle 0, no shift, no glow, AccentDim border.
    private void ResetEggBadge()
    {
        AppBadgeRotate.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, null);
        AppBadgeShift.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null);
        AppBadgeGlow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty, null);
        (AppVersionBadge.BorderBrush as System.Windows.Media.SolidColorBrush)
            ?.BeginAnimation(System.Windows.Media.SolidColorBrush.ColorProperty, null);
    }

    // The payoff: a chamfered "Transmission Intercept" panel with a scanline reveal on the quote.
    // A 1-in-20 roll swaps the amber transmission for the gold priority variant.
    private void ShowEggDialog()
    {
        var gold = Random.Shared.Next(20) == 0;
        Logger.Info($"[UI] easter egg: Words of Wisdom dialog opened ({(gold ? "priority" : "standard")})");

        var dlg = new Window
        {
            Title = "Words of Wisdom",
            Width = EggPanelWidth + EggGlowGutter * 2, Height = EggPanelHeight + EggGlowGutter * 2,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = this,
            ResizeMode = ResizeMode.NoResize, WindowStyle = WindowStyle.None,
            AllowsTransparency = true, ShowInTaskbar = false,
            Background = System.Windows.Media.Brushes.Transparent,
            FontFamily = (System.Windows.Media.FontFamily)Application.Current.FindResource("UiFont"),
        };

        var accentKey = gold ? "GoldBrush" : "AccentBrush";
        var panel = new ChamferPanel
        {
            Width = EggPanelWidth, Height = EggPanelHeight,
            ShowBrackets = true,
            Padding = new Thickness(24, 22, 24, 24),
            Margin = new Thickness(EggGlowGutter),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (gold) panel.BorderBrush = EggBrush("GoldBrush");

        // Panel glow: amber blur 22 at 0.22, gold breathing 0.25 to 0.6 on the rare variant.
        var glow = new System.Windows.Media.Effects.DropShadowEffect
        {
            Color = EggColor(gold ? "GoldColor" : "AccentColor"),
            BlurRadius = 22, ShadowDepth = 0,
            Opacity = gold ? (Motion.Reduced ? 0.4 : 0.25) : 0.22,
        };
        panel.Effect = glow;
        panel.MouseLeftButtonDown += (_, _) => dlg.DragMove();   // no title bar to drag by

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = gold ? "PRIORITY TRANSMISSION" : "INCOMING TRANSMISSION",
            FontFamily = (System.Windows.Media.FontFamily)Application.Current.FindResource("DisplayFont"),
            FontSize = 12, FontWeight = FontWeights.Bold,
            Foreground = EggBrush(accentKey),
        });
        stack.Children.Add(new TextBlock
        {
            Text = "SOURCE: BETA RELAY // CANNONACTUAL",
            FontFamily = (System.Windows.Media.FontFamily)Application.Current.FindResource("MonoFont"),
            FontSize = 9, Foreground = EggBrush("CyanBrush"),
            Margin = new Thickness(0, 6, 0, 0),
        });
        stack.Children.Add(new System.Windows.Shapes.Rectangle
        {
            Height = 1, Fill = EggBrush("NavBorderBrush"), Margin = new Thickness(0, 12, 0, 0),
        });

        var block = new StackPanel();
        block.Children.Add(new TextBlock
        {
            Text = "“No questions until we swap server!”",
            FontSize = 15, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap,
            Foreground = EggBrush(accentKey),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
        });
        block.Children.Add(new TextBlock
        {
            Text = "- CannonActual", FontSize = 11,
            Foreground = EggBrush("FgDimBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 8, 0, 0),
        });

        var beamShift = new System.Windows.Media.TranslateTransform(0, -18);
        var beam = BuildEggBeam(beamShift);
        var wrap = new Grid { Margin = new Thickness(0, 16, 0, 0), ClipToBounds = true };
        wrap.Children.Add(block);
        wrap.Children.Add(beam);
        stack.Children.Add(wrap);

        var okBtn = new Button
        {
            Content = "Understood", Height = 34, HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 20, 0, 0),
            Style = (Style)FindResource("AccentButton"),
        };
        okBtn.Click += (s, e) =>
        {
            InteractionLog.Click("Understood (Words of Wisdom)", okBtn);
            DialogMotion.Close(dlg, dlg.Close);
        };
        stack.Children.Add(okBtn);

        panel.Content = stack;
        var root = new Grid();
        root.Children.Add(panel);
        dlg.Content = root;

        if (Motion.Reduced)
        {
            beam.Visibility = Visibility.Collapsed;
        }
        else
        {
            var clip = new System.Windows.Media.RectangleGeometry(new Rect(0, 0, 0, 0));
            block.Clip = clip;   // held closed until the entrance has settled
            dlg.ContentRendered += (_, _) => StartEggReveal(wrap, clip, beam, beamShift);
            if (gold)
                glow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty,
                    new System.Windows.Media.Animation.DoubleAnimation(0.25, 0.6,
                        new Duration(TimeSpan.FromMilliseconds(Motion.BreatheMs)))
                    {
                        AutoReverse = true,
                        RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
                        EasingFunction = Motion.Breathe,
                    });
        }

        dlg.Closed += (_, _) =>
        {
            glow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty, null);
            ResetEggBadge();
            Logger.Info("[UI] easter egg: Words of Wisdom dialog closed");
        };
        DialogMotion.Attach(dlg);
        UiScaleService.ApplyToDialog(dlg, root);   // App scale (issue #20)
        dlg.ShowDialog();
    }

    // The cyan sweep: an 18px transparent-to-CyanGlow body with a 1px CyanColor leading line.
    private static Grid BuildEggBeam(System.Windows.Media.TranslateTransform shift)
    {
        var cyan = EggColor("CyanColor");
        var body = new System.Windows.Media.LinearGradientBrush
        {
            StartPoint = new Point(0, 0), EndPoint = new Point(0, 1),
        };
        body.GradientStops.Add(new System.Windows.Media.GradientStop(
            System.Windows.Media.Color.FromArgb(0, cyan.R, cyan.G, cyan.B), 0));
        body.GradientStops.Add(new System.Windows.Media.GradientStop(EggColor("CyanDimColor"), 0.45));
        body.GradientStops.Add(new System.Windows.Media.GradientStop(EggColor("CyanGlowColor"), 1));

        var beam = new Grid { Height = 18, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false };
        beam.RenderTransform = shift;
        beam.Children.Add(new System.Windows.Shapes.Rectangle { Fill = body });
        beam.Children.Add(new System.Windows.Shapes.Rectangle
        {
            Height = 1, VerticalAlignment = VerticalAlignment.Bottom,
            Fill = EggBrush("CyanBrush"),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            { Color = cyan, BlurRadius = 10, ShadowDepth = 0, Opacity = 0.267 },
        });
        return beam;
    }

    // Scanline reveal: the quote block unclips top to bottom over 600ms while the beam sweeps
    // down it, then the block holo-flickers. Starts once the dialog entrance has settled.
    private static void StartEggReveal(FrameworkElement wrap,
                                       System.Windows.Media.RectangleGeometry clip,
                                       UIElement beam,
                                       System.Windows.Media.TranslateTransform beamShift)
    {
        double w = wrap.ActualWidth, h = wrap.ActualHeight;
        if (w <= 0 || h <= 0) { clip.Rect = new Rect(0, 0, 10000, 10000); return; }   // never leave it hidden

        var start = TimeSpan.FromMilliseconds(Motion.DialogOpenMs);
        var sweep = new Duration(TimeSpan.FromMilliseconds(600));

        clip.Rect = new Rect(0, 0, w, 0);
        clip.BeginAnimation(System.Windows.Media.RectangleGeometry.RectProperty,
            new System.Windows.Media.Animation.RectAnimation(new Rect(0, 0, w, 0), new Rect(0, 0, w, h), sweep)
            { BeginTime = start, EasingFunction = Motion.Reveal });

        beamShift.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
            new System.Windows.Media.Animation.DoubleAnimation(-18, h, sweep)
            { BeginTime = start, EasingFunction = Motion.Reveal });

        var fade = new System.Windows.Media.Animation.DoubleAnimationUsingKeyFrames
        { BeginTime = start, Duration = sweep };
        fade.KeyFrames.Add(EggLinearKey(1, 0));
        fade.KeyFrames.Add(EggLinearKey(1, 480));   // fades out over the last 120ms
        fade.KeyFrames.Add(EggLinearKey(0, 600));
        beam.BeginAnimation(UIElement.OpacityProperty, fade);

        var flicker = new System.Windows.Media.Animation.DoubleAnimationUsingKeyFrames
        { BeginTime = start + sweep.TimeSpan, Duration = new Duration(TimeSpan.FromMilliseconds(160)) };
        flicker.KeyFrames.Add(EggStepKey(1.0, 0));
        flicker.KeyFrames.Add(EggStepKey(0.55, 40));
        flicker.KeyFrames.Add(EggStepKey(1.0, 80));
        flicker.KeyFrames.Add(EggStepKey(0.75, 120));
        flicker.KeyFrames.Add(EggStepKey(1.0, 160));
        wrap.BeginAnimation(UIElement.OpacityProperty, flicker);
    }

    private static System.Windows.Media.Animation.LinearDoubleKeyFrame EggLinearKey(double value, double ms)
        => new(value, System.Windows.Media.Animation.KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ms)));

    private static System.Windows.Media.Animation.DiscreteDoubleKeyFrame EggStepKey(double value, double ms)
        => new(value, System.Windows.Media.Animation.KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ms)));

}
