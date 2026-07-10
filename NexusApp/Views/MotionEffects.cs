using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace NexusApp.Views;

// One entrance/exit signature for every modal dialog, instead of six hand-rolled
// storyboards. Values frozen in docs/superpowers/specs/2026-07-10-motion-pass-values.md.
public static class DialogMotion
{
    private const double OpenScaleFrom = 0.98;

    // Call after the dialog's content is assigned, before Show/ShowDialog.
    public static void Attach(Window w)
    {
        if (Motion.Reduced || w.Content is not FrameworkElement root) return;
        var scale = new ScaleTransform(OpenScaleFrom, OpenScaleFrom);
        root.RenderTransform = scale;
        root.RenderTransformOrigin = new Point(0.5, 0.5);
        root.Opacity = 0;
        w.Loaded += (_, _) =>
        {
            var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(Motion.DialogOpenMs)) { EasingFunction = Motion.Settle };
            var grow = new DoubleAnimation(OpenScaleFrom, 1, TimeSpan.FromMilliseconds(Motion.DialogOpenMs)) { EasingFunction = Motion.Settle };
            root.BeginAnimation(UIElement.OpacityProperty, fade);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        };
    }

    // Animated close; Reduced closes immediately. Guard against double-invoke.
    public static void Close(Window w, Action close)
    {
        if (Motion.Reduced || w.Content is not FrameworkElement root) { close(); return; }
        var done = false;
        var fade = new DoubleAnimation(root.Opacity, 0, TimeSpan.FromMilliseconds(Motion.DialogCloseMs)) { EasingFunction = Motion.SlideOut };
        fade.Completed += (_, _) => { if (!done) { done = true; close(); } };
        root.BeginAnimation(UIElement.OpacityProperty, fade);
    }
}

// Reusable exit animations for elements that remove themselves from a live panel
// (e.g. a refinery order card once its job completes). Kept alongside DialogMotion
// rather than in Motion.cs, which stays pure vocabulary (constants + easings).
public static class MotionEffects
{
    /// <summary>Fade + vertical collapse, then remove from the panel. Reduced removes instantly.</summary>
    public static void CollapseRemove(FrameworkElement el, Panel parent)
    {
        if (Motion.Reduced) { parent.Children.Remove(el); return; }
        el.IsHitTestVisible = false;
        var scale = new ScaleTransform(1, 1);
        el.RenderTransform = scale;
        el.RenderTransformOrigin = new Point(0.5, 0);
        var ms = TimeSpan.FromMilliseconds(Motion.ExitMs);
        var fade = new DoubleAnimation(el.Opacity, 0, ms) { EasingFunction = Motion.SlideOut };
        var squash = new DoubleAnimation(1, 0, ms) { EasingFunction = Motion.SlideOut };
        fade.Completed += (_, _) => parent.Children.Remove(el);
        el.BeginAnimation(UIElement.OpacityProperty, fade);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, squash);
    }
}
