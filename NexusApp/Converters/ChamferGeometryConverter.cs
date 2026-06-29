using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using NexusApp.Views;

namespace NexusApp.Converters;

/// <summary>
/// MultiBinding [ActualWidth, ActualHeight] + ConverterParameter (chamfer px) -> a TL+BR chamfer
/// <see cref="Geometry"/>, so a <c>Path</c> in a ControlTemplate can draw the MOBIGLAS bevel that tracks
/// the control's size. Lets chamfered buttons / text fields share the exact silhouette as
/// <see cref="ChamferPanel"/> / <see cref="Hud.ChamferGeometry"/> without per-control code-behind.
/// Bind both sources to the TemplatedParent's ActualWidth/ActualHeight.
/// </summary>
public sealed class ChamferGeometryConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not double w || values[1] is not double h)
            return Geometry.Empty;

        double c = 8;
        if (parameter is string s && double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var p))
            c = p;
        else if (parameter is double pd)
            c = pd;

        return Hud.ChamferGeometry(w, h, c);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
