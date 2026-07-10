using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace NexusApp.Views;

// Small modal that collects the metadata for a .nexusgrid export: the contributor's RSI handle
// (pre-filled from the detected handle, editable), a one-line change summary, and free-form notes.
public sealed class GridExportDialog : Window
{
    private readonly TextBox _handle = new();
    private readonly TextBox _summary = new();
    private readonly TextBox _notes = new();
    private readonly CheckBox _flag = new();
    private readonly TextBox _flagNote = new();

    public string Handle => _handle.Text.Trim();
    public string Summary => _summary.Text.Trim();
    public string Notes => _notes.Text.Trim();
    public bool Flagged => _flag.IsChecked == true;
    public string FlagNote => _flagNote.Text.Trim();

    public GridExportDialog(string shipName, string handle)
    {
        Title = $"Export grid layout - {shipName}";
        Width = 460; Height = 500; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x10, 0x17));
        ResizeMode = ResizeMode.NoResize;
        FontFamily = (FontFamily)Application.Current.FindResource("UiFont");

        var stack = new StackPanel { Margin = new Thickness(18) };
        var fg = new SolidColorBrush(Color.FromRgb(0xEA, 0xF1, 0xF6));

        void Lbl(string t) => stack.Children.Add(new TextBlock
        {
            Text = t, Foreground = fg, Margin = new Thickness(0, 10, 0, 4), FontSize = 12,
        });

        _handle.Text = handle;
        Lbl("RSI handle");
        stack.Children.Add(_handle);
        Lbl("Change summary (one line)");
        stack.Children.Add(_summary);
        Lbl("Notes");
        _notes.AcceptsReturn = true; _notes.Height = 90; _notes.TextWrapping = TextWrapping.Wrap;
        _notes.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        stack.Children.Add(_notes);

        // Optional: flag this ship as having a layout problem, with a note for the owner. Rides in the
        // exported file so the owner sees it in the import review.
        _flag.Content = "Flag an issue with this ship's layout";
        _flag.Foreground = fg; _flag.Margin = new Thickness(0, 14, 0, 4);
        stack.Children.Add(_flag);
        Lbl("Flag note (what is wrong)");
        _flagNote.AcceptsReturn = true; _flagNote.Height = 56; _flagNote.TextWrapping = TextWrapping.Wrap;
        _flagNote.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _flagNote.IsEnabled = false;
        _flag.Checked += (_, _) => _flagNote.IsEnabled = true;
        _flag.Unchecked += (_, _) => _flagNote.IsEnabled = false;
        stack.Children.Add(_flagNote);

        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var ok = new Button { Content = "Export", Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(14, 5, 14, 5), IsCancel = true };
        ok.Click += (_, _) => { DialogResult = true; };
        row.Children.Add(ok); row.Children.Add(cancel);
        stack.Children.Add(row);

        foreach (var tb in new[] { _handle, _summary, _notes, _flagNote })
        {
            tb.Background = new SolidColorBrush(Color.FromRgb(0x0E, 0x14, 0x1C));
            tb.Foreground = fg;
            tb.BorderBrush = new SolidColorBrush(Color.FromRgb(0x1C, 0x28, 0x36));
            tb.Padding = new Thickness(6, 4, 6, 4);
        }

        Content = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        DialogMotion.Attach(this);
    }
}
