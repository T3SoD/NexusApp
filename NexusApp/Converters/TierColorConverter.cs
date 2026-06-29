using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using NexusApp.Models;

namespace NexusApp.Converters;

[ValueConversion(typeof(string), typeof(Brush))]
public class TierColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value?.ToString() switch
        {
            "S" => new SolidColorBrush(Color.FromRgb(0xFF, 0xD7, 0x00)),
            "A" => new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50)),
            "B" => new SolidColorBrush(Color.FromRgb(0x29, 0xB6, 0xF6)),
            _   => new SolidColorBrush(Colors.White),
        };
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

[ValueConversion(typeof(string), typeof(Brush))]
public class RarityColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value?.ToString() switch
        {
            "legendary" => new SolidColorBrush(Color.FromRgb(0xFF, 0xD7, 0x00)),
            "epic"      => new SolidColorBrush(Color.FromRgb(0xA8, 0x55, 0xF7)),
            "rare"      => new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6)),
            "uncommon"  => new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E)),
            _           => new SolidColorBrush(Color.FromRgb(0x9E, 0x9E, 0x9E)),
        };
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

[ValueConversion(typeof(string), typeof(Brush))]
public class SystemColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value?.ToString() switch
        {
            "Stanton" => new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6)),
            "Pyro"    => new SolidColorBrush(Color.FromRgb(0xF9, 0x73, 0x16)),
            "Nyx"     => new SolidColorBrush(Color.FromRgb(0xA8, 0x55, 0xF7)),
            _         => new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E)),
        };
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

[ValueConversion(typeof(int), typeof(Brush))]
public class RefineryModifierColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is int mod)
        {
            if (mod > 0) return new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E));
            if (mod < 0) return new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
        }
        return new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E));
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

[ValueConversion(typeof(bool), typeof(System.Windows.Visibility))]
public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

[ValueConversion(typeof(WorkOrderStatus), typeof(Brush))]
public class WorkOrderStatusColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is WorkOrderStatus s ? s switch
        {
            WorkOrderStatus.Mining         => new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6)), // blue
            WorkOrderStatus.Refining       => new SolidColorBrush(Color.FromRgb(0xE6, 0x7E, 0x22)), // orange
            WorkOrderStatus.ReadyToCollect => new SolidColorBrush(Color.FromRgb(0x2E, 0xCC, 0x71)), // green
            WorkOrderStatus.Complete       => new SolidColorBrush(Color.FromRgb(0x7F, 0x8C, 0x8D)), // gray
            _                              => new SolidColorBrush(Color.FromRgb(0x7F, 0x8C, 0x8D)),
        } : new SolidColorBrush(Color.FromRgb(0x7F, 0x8C, 0x8D));
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

// Converts a "#RRGGBB" hex string to a SolidColorBrush - used for dynamic card border colors
[ValueConversion(typeof(string), typeof(Brush))]
public class HexColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        try
        {
            var c = (Color)ColorConverter.ConvertFromString(value?.ToString() ?? "#8B949E");
            return new SolidColorBrush(c);
        }
        catch { return new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E)); }
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}
