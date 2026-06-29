using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;

namespace NexusApp.Views;

/// <summary>
/// XAML-usable MOBIGLAS chamfered panel: a ContentControl whose template draws a Path that bevels the
/// top-left + bottom-right corners (re-computed on resize), with optional amber corner brackets on the
/// two square corners. Mirrors the code-built <see cref="Hud.Panel"/> so XAML surfaces (RS Decoder,
/// overlay) share the exact same panel silhouette as the code-built pages.
///
/// Usage:  &lt;views:ChamferPanel Chamfer="13" ShowBrackets="True" Accent="True" Padding="20,14"&gt; ... &lt;/&gt;
///   Chamfer      - bevel size in px (default 12).
///   ShowBrackets - reveal amber L-brackets on the top-right + bottom-left corners.
///   Accent       - amber stroke + subtle amber-to-bg gradient fill (focal/hero panels).
/// Background / BorderBrush / Padding behave as usual and feed the template.
/// </summary>
public sealed class ChamferPanel : ContentControl
{
    public static readonly DependencyProperty ChamferProperty =
        DependencyProperty.Register(nameof(Chamfer), typeof(double), typeof(ChamferPanel),
            new PropertyMetadata(12.0, OnChamferChanged));

    public double Chamfer
    {
        get => (double)GetValue(ChamferProperty);
        set => SetValue(ChamferProperty, value);
    }

    public static readonly DependencyProperty ShowBracketsProperty =
        DependencyProperty.Register(nameof(ShowBrackets), typeof(bool), typeof(ChamferPanel),
            new PropertyMetadata(false));

    public bool ShowBrackets
    {
        get => (bool)GetValue(ShowBracketsProperty);
        set => SetValue(ShowBracketsProperty, value);
    }

    public static readonly DependencyProperty AccentProperty =
        DependencyProperty.Register(nameof(Accent), typeof(bool), typeof(ChamferPanel),
            new PropertyMetadata(false));

    public bool Accent
    {
        get => (bool)GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    private Path? _frame;

    public ChamferPanel()
    {
        SizeChanged += (_, _) => UpdateGeometry();
    }

    private static void OnChamferChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((ChamferPanel)d).UpdateGeometry();

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _frame = GetTemplateChild("PART_Frame") as Path;
        UpdateGeometry();
    }

    private void UpdateGeometry()
    {
        if (_frame != null)
            _frame.Data = Hud.ChamferGeometry(ActualWidth, ActualHeight, Chamfer);
    }
}
