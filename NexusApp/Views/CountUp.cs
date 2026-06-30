using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace NexusApp.Views;

/// <summary>
/// Attached behavior that animates a TextBlock's number from 0 up to a target value: a quick
/// "lock-on" count-up for the RS Decoder best-match readout. Set <c>CountUp.To</c> to the final
/// value (e.g. bound to the matched RS value); the behavior animates an internal Current value
/// 0 -> To and writes the rounded N0 string into the TextBlock. Re-runs whenever To changes
/// (i.e. each time a new deposit is detected).
///
///   &lt;TextBlock views:CountUp.To="{Binding Resource.BaseRs}" .../&gt;
/// </summary>
public static class CountUp
{
    public static readonly DependencyProperty ToProperty = DependencyProperty.RegisterAttached(
        "To", typeof(double), typeof(CountUp), new PropertyMetadata(double.NaN, OnToChanged));

    public static void SetTo(DependencyObject o, double value) => o.SetValue(ToProperty, value);
    public static double GetTo(DependencyObject o) => (double)o.GetValue(ToProperty);

    // Optional unit appended after the number (e.g. "%"). Set it BEFORE To.
    public static readonly DependencyProperty SuffixProperty = DependencyProperty.RegisterAttached(
        "Suffix", typeof(string), typeof(CountUp), new PropertyMetadata(""));

    public static void SetSuffix(DependencyObject o, string value) => o.SetValue(SuffixProperty, value);
    public static string GetSuffix(DependencyObject o) => (string)o.GetValue(SuffixProperty);

    // Internal animated value; each tick writes the formatted number into the TextBlock.
    private static readonly DependencyProperty CurrentProperty = DependencyProperty.RegisterAttached(
        "Current", typeof(double), typeof(CountUp), new PropertyMetadata(0.0, OnCurrentChanged));

    private static void OnToChanged(DependencyObject o, DependencyPropertyChangedEventArgs e)
    {
        if (o is not TextBlock tb) return;
        double to = (double)e.NewValue;
        if (double.IsNaN(to)) return;

        // Reduce animations: snap straight to the final value, no count-up.
        if (Motion.Reduced)
        {
            tb.BeginAnimation(CurrentProperty, null);
            tb.Text = to.ToString("N0") + GetSuffix(tb);
            return;
        }

        // Restart from 0 each time a new value is detected, then settle up with the mock count-up curve.
        tb.BeginAnimation(CurrentProperty, null);
        tb.SetValue(CurrentProperty, 0.0);
        tb.Text = "0" + GetSuffix(tb);
        var anim = new DoubleAnimation(0, to, new Duration(TimeSpan.FromMilliseconds(Motion.CountUpMs)))
        {
            EasingFunction = Motion.Settle,
        };
        tb.BeginAnimation(CurrentProperty, anim);
    }

    private static void OnCurrentChanged(DependencyObject o, DependencyPropertyChangedEventArgs e)
    {
        if (o is TextBlock tb) tb.Text = ((double)e.NewValue).ToString("N0") + GetSuffix(tb);
    }
}
