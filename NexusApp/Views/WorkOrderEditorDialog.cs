using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NexusApp.Models;
using NexusApp.ViewModels;

namespace NexusApp.Views;

/// <summary>
/// Modal popup editor for a refinery work order. Hosts the shared WorkOrderEditorPanel and drives
/// Save / Delete / Cancel, matching the MOBIGLAS mock's "add/edit in a popup" refinery flow (the page
/// itself is now a card gallery with no inline editor). Save and Delete persist through the panel's VM
/// commands, which raise CollectionChanged so the gallery rebuilds itself automatically.
/// </summary>
public sealed class WorkOrderEditorDialog : Window
{
    private readonly WorkOrderEditorPanel _editor;

    public WorkOrderEditorDialog(WorkOrder order, MainViewModel vm, Window? owner)
    {
        _editor = new WorkOrderEditorPanel(order, vm);

        Owner = owner;
        Title = "Work order";
        Width = 540;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 780;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;
        Background = (Brush)Application.Current.FindResource("BgBrush");
        Foreground = (Brush)Application.Current.FindResource("FgBrush");
        FontFamily = (FontFamily)Application.Current.FindResource("UiFont");

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 660,
            Content = _editor,
        };
        Grid.SetRow(scroll, 0);
        root.Children.Add(scroll);

        // Action row: Delete (left, existing orders only) | Cancel + Save (right).
        var actions = new Grid { Margin = new Thickness(16, 8, 16, 16) };
        var del = MakeButton("Delete", "NexusButton");
        del.HorizontalAlignment = HorizontalAlignment.Left;
        del.Visibility = _editor.IsNewOrder ? Visibility.Collapsed : Visibility.Visible;
        del.Click += (_, _) => { _editor.Delete(); DialogResult = true; Close(); };
        actions.Children.Add(del);

        var rightBtns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = MakeButton("Cancel", "NexusButton");
        cancel.Margin = new Thickness(0, 0, 8, 0);
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        var save = MakeButton("Save", "AccentButton");
        save.Click += (_, _) => { _editor.Save(); DialogResult = true; Close(); };
        rightBtns.Children.Add(cancel);
        rightBtns.Children.Add(save);
        actions.Children.Add(rightBtns);

        Grid.SetRow(actions, 1);
        root.Children.Add(actions);

        Content = root;
    }

    private static Button MakeButton(string text, string styleKey) => new()
    {
        Content = text,
        Style = (Style)Application.Current.FindResource(styleKey),
        Height = 34,
        Padding = new Thickness(18, 0, 18, 0),
        MinWidth = 88,
    };
}
