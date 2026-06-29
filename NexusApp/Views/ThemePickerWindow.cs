using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace NexusApp.Views;

/// <summary>
/// First-run "choose your look" picker, shown once on a fresh install from
/// App.OnStartup BEFORE the main window is built - so the app opens directly in
/// the chosen theme with no restart and the welcome tour runs in that theme.
/// Styled with its own explicit colors (not theme brushes) so it renders the
/// same regardless of which palette happens to be loaded. The card layout keeps
/// the logo as the hero with a screenshot slot reserved above it for later.
/// Returns the chosen theme via <see cref="SelectedTheme"/> ("luxury" | "classic");
/// defaults to luxury if the dialog is dismissed without an explicit choice.
/// </summary>
public class ThemePickerWindow : Window
{
    /// <summary>The theme the user chose. Defaults to luxury when the dialog is dismissed.</summary>
    public string SelectedTheme { get; private set; } = "luxury";

    // Self-contained chrome palette (theme-neutral dark) so the picker looks the
    // same whichever theme the user is about to pick.
    private static readonly Brush ChromeBg     = Hex("#FF0E0E13");
    private static readonly Brush ChromeCard   = Hex("#FF17161E");
    private static readonly Brush ChromeFg     = Hex("#FFECE7DD");
    private static readonly Brush ChromeFgDim  = Hex("#FF8C887F");
    private static readonly Brush ChromeBorder = Hex("#FF2A2833");

    private static readonly Color LuxuryAccent  = (Color)ColorConverter.ConvertFromString("#FFC9A24B");
    private static readonly Color ClassicAccent = (Color)ColorConverter.ConvertFromString("#FF00C9A7");

    private readonly Border _luxuryCard;
    private readonly Border _classicCard;

    public ThemePickerWindow()
    {
        Title = "Welcome to Nexus";
        Width = 660; Height = 540;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen; // no owner: shown pre-main-window
        Background = ChromeBg;
        Foreground = ChromeFg;
        PreviewKeyDown += OnKeyDown;

        var root = new Grid { Margin = new Thickness(28) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // header
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });  // cards
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // footer

        // ── Header ────────────────────────────────────────────────────────────────
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 22) };
        header.Children.Add(new TextBlock
        {
            Text = "Welcome to Nexus",
            FontSize = 24, FontWeight = FontWeights.Bold,
            Foreground = ChromeFg,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        header.Children.Add(new TextBlock
        {
            Text = "Choose your look - you can change it anytime in Settings.",
            FontSize = 13,
            Foreground = ChromeFgDim,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 6, 0, 0),
        });
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        // ── Cards ─────────────────────────────────────────────────────────────────
        var cards = new Grid();
        cards.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        cards.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _luxuryCard = BuildCard(
            theme: "luxury",
            logoUri: "pack://application:,,,/Assets/nexus_logo.png",
            name: "LUXURY",
            description: "Gold on deep black",
            swatches: new[] { "#FF0E0E13", "#FFC9A24B", "#FFD9B25C", "#FFECE7DD" });
        _luxuryCard.Margin = new Thickness(0, 0, 10, 0);
        Grid.SetColumn(_luxuryCard, 0);

        _classicCard = BuildCard(
            theme: "classic",
            logoUri: "pack://application:,,,/Assets/nexus_logo_classic.png",
            name: "CLASSIC",
            description: "Teal & amber on slate",
            swatches: new[] { "#FF0D1117", "#FF00C9A7", "#FFE8A23A", "#FFE6EDF3" });
        _classicCard.Margin = new Thickness(10, 0, 0, 0);
        Grid.SetColumn(_classicCard, 1);

        cards.Children.Add(_luxuryCard);
        cards.Children.Add(_classicCard);
        Grid.SetRow(cards, 1);
        root.Children.Add(cards);

        // ── Footer ────────────────────────────────────────────────────────────────
        var footer = new Grid { Margin = new Thickness(0, 20, 0, 0) };
        var continueBtn = FlatButton("Continue", Hex("#FFECE7DD"), Hex("#FF0E0E13"));
        continueBtn.HorizontalAlignment = HorizontalAlignment.Right;
        continueBtn.Click += (s, e) => Close();   // SelectedTheme already reflects the choice
        footer.Children.Add(continueBtn);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        Content = root;
        UpdateSelection();
    }

    /// <summary>Builds one selectable theme card. The logo is the hero; a screenshot
    /// can be slotted in above it later without restructuring.</summary>
    private Border BuildCard(string theme, string logoUri, string name, string description, string[] swatches)
    {
        var inner = new StackPanel { Margin = new Thickness(20, 24, 20, 24) };

        inner.Children.Add(new Image
        {
            Source = SafeImage(logoUri),
            Height = 120,
            Stretch = Stretch.Uniform,
            Margin = new Thickness(0, 0, 0, 16),
        });
        inner.Children.Add(new TextBlock
        {
            Text = name,
            FontSize = 18, FontWeight = FontWeights.Bold,
            Foreground = ChromeFg,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        inner.Children.Add(new TextBlock
        {
            Text = description,
            FontSize = 12,
            Foreground = ChromeFgDim,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 16),
        });

        var strip = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var hex in swatches)
            strip.Children.Add(new Border
            {
                Width = 24, Height = 24,
                CornerRadius = new CornerRadius(4),
                Margin = new Thickness(3, 0, 3, 0),
                Background = Hex(hex),
                BorderBrush = ChromeBorder,
                BorderThickness = new Thickness(1),
            });
        inner.Children.Add(strip);

        var card = new Border
        {
            Background = ChromeCard,
            CornerRadius = new CornerRadius(10),
            BorderBrush = ChromeBorder,
            BorderThickness = new Thickness(2),
            Cursor = Cursors.Hand,
            Child = inner,
            Tag = theme,
        };
        card.MouseLeftButtonUp += (s, e) => { SelectedTheme = theme; UpdateSelection(); };
        return card;
    }

    private void UpdateSelection()
    {
        StyleCard(_luxuryCard, LuxuryAccent, SelectedTheme == "luxury");
        StyleCard(_classicCard, ClassicAccent, SelectedTheme == "classic");
    }

    private static void StyleCard(Border card, Color accent, bool selected)
    {
        if (selected)
        {
            card.BorderBrush = new SolidColorBrush(accent);
            card.BorderThickness = new Thickness(2.5);
            card.Effect = new DropShadowEffect { Color = accent, BlurRadius = 22, ShadowDepth = 0, Opacity = 0.55 };
        }
        else
        {
            card.BorderBrush = ChromeBorder;
            card.BorderThickness = new Thickness(2);
            card.Effect = null;
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Left:  SelectedTheme = "luxury";  UpdateSelection(); break;
            case Key.Right: SelectedTheme = "classic"; UpdateSelection(); break;
            case Key.Enter:
            case Key.Space:
            case Key.Escape: Close(); break;
        }
    }

    /// <summary>A flat, rounded button that renders the same regardless of OS theme
    /// (the default button chrome is replaced).</summary>
    private static Button FlatButton(string text, Brush bg, Brush fg)
    {
        var border = new System.Windows.FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, bg);
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        border.SetValue(Border.PaddingProperty, new Thickness(28, 10, 28, 10));
        var content = new System.Windows.FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content);

        return new Button
        {
            Content = text,
            Foreground = fg,
            FontSize = 14, FontWeight = FontWeights.SemiBold,
            Cursor = Cursors.Hand,
            Template = new ControlTemplate(typeof(Button)) { VisualTree = border },
        };
    }

    private static ImageSource? SafeImage(string uri)
    {
        try { return new BitmapImage(new Uri(uri)); }
        catch { return null; }
    }

    private static SolidColorBrush Hex(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
