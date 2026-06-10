using System.Windows;
using System.Windows.Controls;
using NexusApp.ViewModels;

namespace NexusApp.Views;

public class ShoppingDialog : Window
{
    private readonly MainViewModel _vm;
    private StackPanel _list = new();

    public ShoppingDialog(MainViewModel vm)
    {
        _vm = vm;
        Title = "Shopping List";
        Width = 480; Height = 500;
        Background = (System.Windows.Media.Brush)Application.Current.FindResource("BgBrush");
        Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("FgBrush");
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var outer = new Grid();
        outer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(16) };
        scroll.Content = _list;
        outer.Children.Add(scroll);

        var footer = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16, 0, 16, 16) };
        var clearBtn = new Button
        {
            Content = "Clear All",
            Style = (Style)Application.Current.FindResource("NexusButton"),
            Padding = new Thickness(12, 6, 12, 6),
        };
        clearBtn.Click += (s, e) => { _vm.ClearShoppingCommand.Execute(null); Rebuild(); };
        footer.Children.Add(clearBtn);
        Grid.SetRow(footer, 1);
        outer.Children.Add(footer);

        Content = outer;
        Rebuild();
    }

    private void Rebuild()
    {
        _list.Children.Clear();
        double shipTotal = 0;
        int itemCount = 0;

        foreach (var item in _vm.ShoppingList)
        {
            var row = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            row.Children.Add(new TextBlock
            {
                Text = item.ResourceName,
                FontSize = 13, VerticalAlignment = VerticalAlignment.Center,
            });

            var qty = new TextBlock
            {
                Text = $"{item.Quantity:0.##} {item.Unit}",
                FontSize = 12,
                Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("FgDimBrush"),
                Margin = new Thickness(8, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(qty, 1);
            row.Children.Add(qty);

            var rmBtn = new Button
            {
                Content = "−",
                Style = (Style)Application.Current.FindResource("NexusButton"),
                Padding = new Thickness(6, 2, 6, 2), Tag = item.ResourceName,
            };
            rmBtn.Click += (s, e) =>
            {
                _vm.RemoveFromShoppingCommand.Execute(((Button)s).Tag);
                Rebuild();
            };
            Grid.SetColumn(rmBtn, 2);
            row.Children.Add(rmBtn);

            _list.Children.Add(row);

            if (item.Unit == "SCU") shipTotal += item.Quantity;
            else itemCount += (int)item.Quantity;
        }

        // Total
        _list.Children.Add(new Border
        {
            Height = 1, Background = (System.Windows.Media.Brush)Application.Current.FindResource("BorderBrush"),
            Margin = new Thickness(0, 12, 0, 8),
        });
        _list.Children.Add(new TextBlock
        {
            Text = $"Total: {shipTotal:0.##} SCU ship resources" + (itemCount > 0 ? $"  ·  {itemCount}× items" : ""),
            FontSize = 12, FontWeight = FontWeights.SemiBold,
        });
    }
}
