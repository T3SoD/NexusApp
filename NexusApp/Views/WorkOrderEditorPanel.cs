using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using NexusApp.Models;
using NexusApp.ViewModels;

namespace NexusApp.Views;

public class WorkOrderEditorPanel : UserControl
{
    public static event Action<string>? OrderReadyToCollect;

    private readonly WorkOrder _order;
    private readonly MainViewModel _vm;
    private readonly List<string> _allLocations;

    private readonly TextBox _labelBox;
    private readonly TextBox _resourcesBox;
    private readonly TextBox _locationBox;
    private readonly ComboBox _refineryBox;
    private readonly TextBox _notesBox;
    private readonly TextBox _timerHours;
    private readonly TextBox _timerMinutes;

    // Status pills
    private readonly RadioButton _pillRefining;
    private readonly RadioButton _pillReady;
    private readonly RadioButton _pillComplete;

    // Timer display
    private readonly TextBlock _timerCountdown;
    private readonly ScaleTransform _progressScale = new(0, 1);
    private DispatcherTimer? _ticker;

    // Autocomplete
    private Popup? _resourcesPopup;
    private ListBox? _resourcesSuggestList;
    private Popup? _locationPopup;
    private ListBox? _locationSuggestList;
    private bool _suppressResourcesAC;
    private bool _suppressLocationAC;

    private static readonly string[] Refineries =
    [
        "ARC-L1 Wide Forest Station", "ARC-L2 Lively Pathway Station",
        "ARC-L4 Faint Glen Station", "CRU-L1 Ambitious Dream Station",
        "HUR-L1 Green Glade Station", "MIC-L1 Shallow Frontier Station",
        "MIC-L2 Long Forest Station", "MIC-L5 Modern Icarus Station",
        "Levski", "Nyx Gateway (Stanton)", "Nyx Gateway (Pyro)",
        "Stanton Gateway (Nyx)", "Stanton Gateway (Pyro)",
        "Pyro Gateway (Nyx)", "Pyro Gateway (Stanton)",
        "Terra Gateway (Stanton)", "Ruin Station", "Orbituary", "Checkmate",
    ];

    public WorkOrderEditorPanel(WorkOrder order, MainViewModel vm)
    {
        _order = order;
        _vm = vm;

        _allLocations = vm.AllResources
            .SelectMany(r => r.Locations)
            .Distinct()
            .OrderBy(l => l)
            .ToList();

        _labelBox     = MakeTextBox();
        _resourcesBox = MakeTextBox();
        _locationBox  = MakeTextBox();
        _notesBox     = MakeTextBox(height: 80, multiLine: true);
        _timerHours   = MakeTextBox(width: 54);
        _timerMinutes = MakeTextBox(width: 54);
        _refineryBox  = MakeComboBox();

        _pillRefining = MakePill("Refining");
        _pillReady    = MakePill("Ready to Collect");
        _pillComplete = MakePill("Complete");

        _timerCountdown = new TextBlock
        {
            FontSize = 22, FontWeight = FontWeights.Bold, FontFamily = (System.Windows.Media.FontFamily)System.Windows.Application.Current.FindResource("HeadFont"),
            Foreground = (Brush)Application.Current.FindResource("AccentBrush"),
            Margin = new Thickness(0, 4, 0, 4),
        };
        // progress bar built in BuildUI — _progressScale already initialized

        BuildUI();
        SetupResourcesAutocomplete();
        SetupLocationAutocomplete();
        StartTicker();
    }

    private UIElement BuildSummaryCard()
    {
        var inner = new StackPanel();
        inner.Children.Add(new TextBlock
        {
            Text = "ORDER SUMMARY", FontSize = 10, FontWeight = FontWeights.Bold,
            Foreground = (Brush)Application.Current.FindResource("AccentBrush"),
            Margin = new Thickness(0, 0, 0, 8),
        });
        var row = new System.Windows.Controls.Primitives.UniformGrid { Columns = 4 };
        void Cell(string k, string v, Brush? valColor = null)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };
            sp.Children.Add(new TextBlock
            {
                Text = k, FontSize = 10,
                Foreground = (Brush)Application.Current.FindResource("FgDimBrush"),
            });
            sp.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(v) ? "-" : v, FontSize = 14,
                FontFamily = (FontFamily)Application.Current.FindResource("HeadFont"),
                Foreground = valColor ?? (Brush)Application.Current.FindResource("FgBrush"),
                Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis,
            });
            row.Children.Add(sp);
        }
        Cell("Status", _order.StatusLabel, BrushFromHex(_order.StatusColorHex));
        Cell("Time", _order.HasActiveTimer ? _order.TimerRemainingShort : "-");
        Cell("Refinery", _order.Refinery);
        Cell("Location", _order.Location);
        inner.Children.Add(row);

        // Chamfered HUD summary panel (faint amber fill + corner brackets).
        var panel = Hud.Panel(inner, chamfer: 12, brackets: true,
            bg: (Brush)Application.Current.FindResource("AccentDimBrush"),
            border: (Brush)Application.Current.FindResource("AccentStrongBrush"),
            padding: new Thickness(16, 13, 16, 14));
        panel.Margin = new Thickness(0, 4, 0, 14);
        return panel;
    }

    private void BuildUI()
    {
        var stack = new StackPanel { Margin = new Thickness(16) };

        var titleText = !string.IsNullOrWhiteSpace(_order.Label) ? _order.Label
                      : !string.IsNullOrWhiteSpace(_order.Resources) ? _order.Resources : "New Order";
        stack.Children.Add(new TextBlock
        {
            Text = "Work Order - " + titleText,
            FontFamily = (FontFamily)Application.Current.FindResource("HeadFont"),
            FontSize = 20, Foreground = (Brush)Application.Current.FindResource("FgBrush"),
            Margin = new Thickness(0, 0, 0, 4), TextTrimming = TextTrimming.CharacterEllipsis,
        });
        stack.Children.Add(BuildSummaryCard());

        stack.Children.Add(MakeRow("Label", _labelBox));
        _labelBox.Text = _order.Label;

        stack.Children.Add(MakeRow("Resource(s)", _resourcesBox));
        _resourcesBox.Text = _order.Resources;

        stack.Children.Add(MakeRow("Mining Location", _locationBox));
        _locationBox.Text = _order.Location;

        foreach (var r in Refineries) _refineryBox.Items.Add(r);
        _refineryBox.SelectedItem = _order.Refinery;
        stack.Children.Add(MakeRow("Refinery", _refineryBox));

        // Status pills
        var pillGroup = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        pillGroup.Children.Add(new TextBlock
        {
            Text = "Status", FontSize = 11,
            Foreground = (Brush)Application.Current.FindResource("FgDimBrush"),
            Margin = new Thickness(0, 0, 0, 6),
        });
        var pillRow = new WrapPanel { Orientation = Orientation.Horizontal };
        pillRow.Children.Add(_pillRefining);
        pillRow.Children.Add(_pillReady);
        pillRow.Children.Add(_pillComplete);
        pillGroup.Children.Add(pillRow);
        stack.Children.Add(pillGroup);

        SetPillFromStatus(_order.Status);

        // Timer input
        var timerSection = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        timerSection.Children.Add(new TextBlock
        {
            Text = "Refinery Timer", FontSize = 11,
            Foreground = (Brush)Application.Current.FindResource("FgDimBrush"),
            Margin = new Thickness(0, 0, 0, 6),
        });
        var timerInputRow = new StackPanel { Orientation = Orientation.Horizontal };
        timerInputRow.Children.Add(_timerHours);
        timerInputRow.Children.Add(new TextBlock
        {
            Text = "h", FontSize = 13, VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)Application.Current.FindResource("FgDimBrush"),
            Margin = new Thickness(6, 0, 10, 0),
        });
        timerInputRow.Children.Add(_timerMinutes);
        timerInputRow.Children.Add(new TextBlock
        {
            Text = "m", FontSize = 13, VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)Application.Current.FindResource("FgDimBrush"),
            Margin = new Thickness(6, 0, 0, 0),
        });
        timerSection.Children.Add(timerInputRow);
        stack.Children.Add(timerSection);

        if (_order.TimerEnd.HasValue)
        {
            var remaining = _order.TimerEnd.Value - DateTime.UtcNow;
            if (remaining > TimeSpan.Zero)
            {
                _timerHours.Text   = ((int)remaining.TotalHours).ToString();
                _timerMinutes.Text = remaining.Minutes.ToString();
            }
        }

        // Countdown display
        var countdownSection = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        countdownSection.Children.Add(_timerCountdown);

        var accentColor = Hud.Col("AccentBrush");
        var progressTrack = new Grid { Height = 6, Margin = new Thickness(0, 4, 0, 8) };
        progressTrack.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x1C, accentColor.R, accentColor.G, accentColor.B)),
            CornerRadius = new CornerRadius(3),
        });
        progressTrack.Children.Add(new Border
        {
            // MOBIGLAS state bar: amber gradient + glow, width driven by _progressScale.
            Background = new LinearGradientBrush(Color.FromArgb(0xCC, accentColor.R, accentColor.G, accentColor.B), accentColor, 0),
            CornerRadius = new CornerRadius(3),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            RenderTransform = _progressScale,
            RenderTransformOrigin = new Point(0, 0.5),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = accentColor, BlurRadius = 8, ShadowDepth = 0, Opacity = 0.55 },
        });
        countdownSection.Children.Add(progressTrack);
        stack.Children.Add(countdownSection);

        stack.Children.Add(MakeRow("Notes", _notesBox));
        _notesBox.Text = _order.Notes;

        Content = stack;
    }

    public bool IsNewOrder => string.IsNullOrEmpty(_order.Label);

    public void Save()  => Save_Click(this, null!);
    public void Delete() => Delete_Click(this, null!);

    // ── Autocomplete ─────────────────────────────────────────────────────────

    private void SetupResourcesAutocomplete()
    {
        _resourcesSuggestList = BuildSuggestList();
        _resourcesSuggestList.MouseLeftButtonUp += ResourcesSuggest_Click;

        _resourcesPopup = new Popup
        {
            PlacementTarget = _resourcesBox,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
            Child = new Border
            {
                Background = (Brush)Application.Current.FindResource("Bg2NavBrush"),
                BorderBrush = (Brush)Application.Current.FindResource("NavBorderBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(0, 0, 4, 4),
                Child = _resourcesSuggestList,
            },
        };

        _resourcesBox.TextChanged += ResourcesBox_TextChanged;
    }

    private void SetupLocationAutocomplete()
    {
        _locationSuggestList = BuildSuggestList();
        _locationSuggestList.MouseLeftButtonUp += LocationSuggest_Click;

        _locationPopup = new Popup
        {
            PlacementTarget = _locationBox,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
            Child = new Border
            {
                Background = (Brush)Application.Current.FindResource("Bg2NavBrush"),
                BorderBrush = (Brush)Application.Current.FindResource("NavBorderBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(0, 0, 4, 4),
                Child = _locationSuggestList,
            },
        };

        _locationBox.TextChanged += LocationBox_TextChanged;
    }

    private void ResourcesBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressResourcesAC) return;
        var text = _resourcesBox.Text;
        var lastSemi = text.LastIndexOf(';');
        var token = (lastSemi >= 0 ? text[(lastSemi + 1)..] : text).Trim();

        if (token.Length < 2) { _resourcesPopup!.IsOpen = false; return; }

        var matches = _vm.AllResources
            .Where(r => r.Name.Contains(token, StringComparison.OrdinalIgnoreCase))
            .Select(r => r.Name)
            .OrderBy(n => n)
            .Take(12)
            .ToList();

        if (matches.Count == 0) { _resourcesPopup!.IsOpen = false; return; }

        _resourcesSuggestList!.ItemsSource = matches;
        _resourcesPopup!.Width = _resourcesBox.ActualWidth;
        _resourcesPopup.IsOpen = true;
    }

    private void ResourcesSuggest_Click(object sender, MouseButtonEventArgs e)
    {
        if (_resourcesSuggestList!.SelectedItem is not string name) return;

        _suppressResourcesAC = true;
        var parts = _resourcesBox.Text.Split(';');
        parts[^1] = name;
        var newText = string.Join("; ", parts.Select(p => p.Trim()).Where(p => p.Length > 0)) + "; ";
        _resourcesBox.Text = newText;
        _resourcesBox.CaretIndex = newText.Length;
        _resourcesPopup!.IsOpen = false;
        _suppressResourcesAC = false;
        _resourcesBox.Focus();
    }

    private void LocationBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressLocationAC) return;
        var token = _locationBox.Text.Trim();

        if (token.Length < 2) { _locationPopup!.IsOpen = false; return; }

        var matches = _allLocations
            .Where(l => l.Contains(token, StringComparison.OrdinalIgnoreCase))
            .Take(12)
            .ToList();

        if (matches.Count == 0) { _locationPopup!.IsOpen = false; return; }

        _locationSuggestList!.ItemsSource = matches;
        _locationPopup!.Width = _locationBox.ActualWidth;
        _locationPopup.IsOpen = true;
    }

    private void LocationSuggest_Click(object sender, MouseButtonEventArgs e)
    {
        if (_locationSuggestList!.SelectedItem is not string name) return;

        _suppressLocationAC = true;
        _locationBox.Text = name;
        _locationBox.CaretIndex = name.Length;
        _locationPopup!.IsOpen = false;
        _suppressLocationAC = false;
        _locationBox.Focus();
    }

    // ── Timer ────────────────────────────────────────────────────────────────

    private void StartTicker()
    {
        UpdateCountdownDisplay();

        if (_order.TimerEnd.HasValue && _order.TimerStart.HasValue)
        {
            var now = DateTime.UtcNow;
            var remaining = _order.TimerEnd.Value - now;
            var total = (_order.TimerEnd.Value - _order.TimerStart.Value).TotalSeconds;
            var fromFraction = total > 0
                ? Math.Clamp((total - Math.Max(remaining.TotalSeconds, 0)) / total, 0, 1)
                : 0;

            _progressScale.ScaleX = fromFraction;

            if (remaining > TimeSpan.Zero)
            {
                var anim = new DoubleAnimation(fromFraction, 1.0, remaining)
                {
                    FillBehavior = FillBehavior.HoldEnd,
                };
                _progressScale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
            }
            else
            {
                _progressScale.ScaleX = 1.0;
            }
        }

        _ticker = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _ticker.Tick += (s, e) => UpdateCountdownDisplay();
        _ticker.Start();
        Unloaded += (s, e) => _ticker?.Stop();
    }

    private void UpdateCountdownDisplay()
    {
        if (!_order.TimerEnd.HasValue || !_order.TimerStart.HasValue)
        {
            _timerCountdown.Text = "";
            _progressScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            _progressScale.ScaleX = 0;
            return;
        }

        var now = DateTime.UtcNow;
        var remaining = _order.TimerEnd.Value - now;

        if (remaining <= TimeSpan.Zero)
        {
            _timerCountdown.Text = "Ready to Collect";
            _timerCountdown.Foreground = BrushFromHex("#2ECC71");
            _progressScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            _progressScale.ScaleX = 1;

            if (_order.Status == WorkOrderStatus.Refining || _order.Status == WorkOrderStatus.Mining)
            {
                _order.Status = WorkOrderStatus.ReadyToCollect;
                SetPillFromStatus(_order.Status);
                _vm.SaveWorkOrderCommand.Execute(_order);
                OrderReadyToCollect?.Invoke(_order.Label);
            }
            _ticker?.Stop();
            return;
        }

        var h = (int)remaining.TotalHours;
        var m = remaining.Minutes;
        var s = remaining.Seconds;
        _timerCountdown.Text = h > 0 ? $"{h}h {m:D2}m {s:D2}s remaining"
                                      : m > 0 ? $"{m}m {s:D2}s remaining"
                                              : $"{s}s remaining";
        _timerCountdown.Foreground = BrushFromHex("#E67E22");
    }

    // ── Save / Delete ────────────────────────────────────────────────────────

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _order.Label     = _labelBox.Text;
        _order.Resources = _resourcesBox.Text.Trim().TrimEnd(';').Trim();
        _order.Location  = _locationBox.Text;
        _order.Refinery  = _refineryBox.SelectedItem?.ToString() ?? "";
        _order.Status    = GetStatusFromPills();
        _order.Notes     = _notesBox.Text;

        var h = int.TryParse(_timerHours.Text.Trim(),   out var hv) ? hv : 0;
        var m = int.TryParse(_timerMinutes.Text.Trim(), out var mv) ? mv : 0;
        if (h > 0 || m > 0)
        {
            _order.TimerStart = DateTime.UtcNow;
            _order.TimerEnd   = DateTime.UtcNow.AddHours(h).AddMinutes(m);
        }

        _vm.SaveWorkOrderCommand.Execute(_order);
        UpdateCountdownDisplay();
        if (_order.TimerEnd.HasValue) { _ticker?.Stop(); StartTicker(); }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        _ticker?.Stop();
        _vm.DeleteWorkOrderCommand.Execute(_order.Id);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private WorkOrderStatus GetStatusFromPills()
    {
        if (_pillReady.IsChecked    == true) return WorkOrderStatus.ReadyToCollect;
        if (_pillComplete.IsChecked == true) return WorkOrderStatus.Complete;
        return WorkOrderStatus.Refining;
    }

    private void SetPillFromStatus(WorkOrderStatus status)
    {
        // Mining is retired — treat existing Mining orders as Refining
        _pillRefining.IsChecked = status == WorkOrderStatus.Refining || status == WorkOrderStatus.Mining;
        _pillReady.IsChecked    = status == WorkOrderStatus.ReadyToCollect;
        _pillComplete.IsChecked = status == WorkOrderStatus.Complete;
    }

    private static FrameworkElement MakeRow(string label, UIElement control)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        panel.Children.Add(new TextBlock
        {
            Text = label, FontSize = 11,
            Foreground = (Brush)Application.Current.FindResource("FgDimBrush"),
            Margin = new Thickness(0, 0, 0, 3),
        });
        panel.Children.Add(control);
        return panel;
    }

    private static TextBox MakeTextBox(double? width = null, double? height = null, bool multiLine = false)
    {
        var tb = new TextBox { Style = (Style)Application.Current.FindResource("NexusTextBox") };
        if (width.HasValue)  tb.Width = width.Value;
        if (height.HasValue) tb.Height = height.Value;
        if (multiLine) { tb.AcceptsReturn = true; tb.TextWrapping = TextWrapping.Wrap; }
        return tb;
    }

    private static ComboBox MakeComboBox() => new ComboBox
    {
        Style = (Style)Application.Current.FindResource("NexusComboBox"),
    };

    private static RadioButton MakePill(string label) => new RadioButton
    {
        Content = label,
        Style = (Style)Application.Current.FindResource("StatusPill"),
        GroupName = "WorkOrderStatus",
    };

    private static ListBox BuildSuggestList()
    {
        var style = new Style(typeof(ListBoxItem));
        style.Setters.Add(new Setter(ListBoxItem.ForegroundProperty,   Application.Current.FindResource("FgBrush")));
        style.Setters.Add(new Setter(ListBoxItem.BackgroundProperty,   Brushes.Transparent));
        style.Setters.Add(new Setter(ListBoxItem.PaddingProperty,      new Thickness(10, 6, 10, 6)));
        style.Setters.Add(new Setter(ListBoxItem.FontSizeProperty,     13.0));
        var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hoverTrigger.Setters.Add(new Setter(ListBoxItem.BackgroundProperty, Application.Current.FindResource("HighlightBrush")));
        style.Triggers.Add(hoverTrigger);
        var selectedTrigger = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
        selectedTrigger.Setters.Add(new Setter(ListBoxItem.BackgroundProperty, Application.Current.FindResource("AccentDimBrush")));
        style.Triggers.Add(selectedTrigger);

        return new ListBox
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            MaxHeight = 200,
            ItemContainerStyle = style,
        };
    }

    private static SolidColorBrush BrushFromHex(string hex)
    {
        var c = (Color)ColorConverter.ConvertFromString(hex);
        return new SolidColorBrush(c);
    }
}
