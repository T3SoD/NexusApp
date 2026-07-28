using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace NexusApp.Converters;

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

[ValueConversion(typeof(bool), typeof(System.Windows.Visibility))]
public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
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
