using System;
using System.Windows;
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
