using System.Windows.Media;

namespace NexusApp.Views;

/// <summary>
/// Pure presentation helpers shared across the views - colour/brush mapping for
/// rarities, tiers, systems and categories, plus small string/format utilities.
/// Extracted from MainWindow.xaml.cs so the per-page views can share them.
/// </summary>
public static class UiHelpers
{
    public static string GetSystem(string location)
    {
        if (location.StartsWith("Pyro") || location == "Akiro Cluster") return "Pyro";
        if (location is "Glaciem Ring" or "Keeger Belt" or "Levski" or "Breaker Stations Interior" or "Breaker Stations Large Geode") return "Nyx";
        return "Stanton";
    }

    public static string MethodLabel(string method) => method switch
    {
        "ship" => "Ship", "vehicle" => "ROC", "fps" => "FPS", "fps+vehicle" => "FPS · ROC", _ => method,
    };

    public static string CapFirst(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0]) + s[1..];

    public static Brush RarityBrush(string rarity) => rarity switch
    {
        "legendary" => BrushFromHex("#FFD700"), "epic"     => BrushFromHex("#A855F7"),
        "rare"      => BrushFromHex("#3B82F6"), "uncommon" => BrushFromHex("#22C55E"),
        _ => BrushFromHex("#9E9E9E"),
    };

    public static Brush TierBrush(string tier) => tier switch
    {
        "S" => BrushFromHex("#FFD700"), "A" => BrushFromHex("#4CAF50"),
        "B" => BrushFromHex("#29B6F6"), _   => Brushes.White,
    };

    public static Brush SystemBrush(string sys) => sys switch
    {
        "Pyro" => BrushFromHex("#F97316"), "Nyx" => BrushFromHex("#A855F7"),
        _ => BrushFromHex("#3B82F6"),
    };

    public static Brush ModifierBrush(int mod) =>
        mod > 0 ? BrushFromHex("#22C55E") : mod < 0 ? BrushFromHex("#EF4444") : BrushFromHex("#8B949E");

    // MOBIGLAS HUD accent tones (same blue/cyan/green/gold the dashboards' Hud.StateBar + status chips
    // use) so the category counts/bars read as on-theme HUD readouts instead of saturated candy colors.
    public static Brush CategoryBrush(string cat) => cat switch
    {
        "Armor"           => BrushFromHex("#5FA8FF"),  // HUD blue
        "Weapons"         => BrushFromHex("#FF9D4D"),  // HUD warm amber
        "Ship Components" => BrushFromHex("#7FE9E0"),  // HUD cyan
        "Ammo"            => BrushFromHex("#F2C14E"),  // HUD gold
        _                 => BrushFromHex("#8C887F"),  // muted grey
    };

    public static Brush AccentBrush() => (Brush)System.Windows.Application.Current.FindResource("AccentBrush");

    public static SolidColorBrush BrushFromHex(string hex)
    {
        var c = (Color)ColorConverter.ConvertFromString(hex);
        return new SolidColorBrush(c);
    }
}
