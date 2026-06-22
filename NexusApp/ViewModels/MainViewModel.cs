using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NexusApp.Models;
using NexusApp.Services;

namespace NexusApp.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly ScannerService _scanner;

    [ObservableProperty] private string _rsInput = "";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _scanStatusText = "● idle";
    [ObservableProperty] private bool _isScanActive;
    [ObservableProperty] private string _activeNav = "scan";  // scan reference workorders blueprints
    [ObservableProperty] private string _refFilter = "";
    [ObservableProperty] private string _blueprintSearch = "";
    [ObservableProperty] private Resource? _selectedResource;
    [ObservableProperty] private Blueprint? _selectedBlueprint;
    [ObservableProperty] private HistoryFilter _historyFilter = HistoryFilter.All;

    public ObservableCollection<MatchResult> ScanResults { get; } = [];
    public ObservableCollection<ScanHistoryEntry> ScanHistory { get; } = [];
    public ObservableCollection<ScanHistoryEntry> FilteredScanHistory { get; } = [];
    public ObservableCollection<Resource> AllResources { get; } = [];
    public ObservableCollection<WorkOrder> WorkOrders { get; } = [];
    public ObservableCollection<ShoppingItem> ShoppingList { get; } = [];
    public ObservableCollection<Blueprint> BlueprintResults { get; } = [];

    // ── RS Decoder (C1 dashboard) derived views ───────────────────────────────
    public MatchResult? BestMatch => ScanResults.Count > 0 ? ScanResults[0] : null;
    public IEnumerable<MatchResult> OtherMatches => ScanResults.Skip(1);
    public bool HasResults => ScanResults.Count > 0;
    public bool NoResults  => ScanResults.Count == 0;

    private void NotifyScanDerived()
    {
        OnPropertyChanged(nameof(BestMatch));
        OnPropertyChanged(nameof(OtherMatches));
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(NoResults));
    }

    public event Action<int>? OcrValueReceived;
    public event Action<ScanPhase>? OcrPhaseReceived;
    public event Action<int>? OcrProgressReceived;

    partial void OnHistoryFilterChanged(HistoryFilter value) => RebuildFilteredHistory();

    public MainViewModel()
    {
        _scanner = new ScannerService();
        _scanner.ValueDetected     += OnOcrValue;
        _scanner.PhaseChanged      += p => OcrPhaseReceived?.Invoke(p);
        _scanner.CandidateProgress += count => OcrProgressReceived?.Invoke(count);
        _scanner.ScanTick          += OnScanTick;
        _scanner.StatusChanged     += s => ScanStatusText = s;

        LoadAllResources();
        LoadWorkOrders();
        LoadShoppingList();

        ShoppingList.CollectionChanged += (_, _) => RefreshCartStatus();

        // Auto-scan is OPT-IN — deliberately NOT started here. Continuously capturing the screen
        // knocks games running in exclusive fullscreen out of fullscreen (it was tabbing players
        // out mid-session), so the user starts it from the overlay's SCAN tab only when they
        // actually want to decode RS values. IsScanActive defaults to false.
    }

    private void LoadAllResources()
    {
        AllResources.Clear();
        foreach (var r in App.Data.GetAllResources())
            AllResources.Add(r);
    }

    private void LoadWorkOrders()
    {
        WorkOrders.Clear();
        try
        {
            foreach (var wo in App.Data.GetWorkOrders())
                WorkOrders.Add(wo);
        }
        catch { /* schema mismatch on old DB — migration handles it on next launch */ }
    }

    private void LoadShoppingList()
    {
        ShoppingList.Clear();
        foreach (var item in App.Data.GetShoppingList())
            ShoppingList.Add(item);
    }

    [RelayCommand]
    private void Lookup()
    {
        if (!int.TryParse(RsInput.Trim().Replace(",", ""), out var rs)) return;
        RunScan(rs, addToHistory: true);
    }

    public void RunScanNoHistory(int rs)
    {
        RsInput = rs.ToString();
        RunScan(rs, addToHistory: false);
    }

    private void RunScan(int rs, bool addToHistory)
    {
        var cart = CartNames();
        var matches = App.Data.FindByRs(rs);
        ScanResults.Clear();
        foreach (var m in matches)
            ScanResults.Add(new MatchResult(m.Resource, rs, m.Nodes, m.IsExact, m.ErrorPct)
                { IsInCart = cart.Contains(m.Resource.Name) });

        StatusText = matches.Count == 0 ? "No matches found" : "";

        bool isNewScan = ScanHistory.Count == 0 || ScanHistory[0].Rs != rs;
        NotifyScanDerived();

        if (!addToHistory || !isNewScan) return;

        var topName   = matches.Count > 0 ? matches[0].Resource.Name : "No match";
        var matchKind = matches.Count == 0 ? MatchKind.None
                      : matches[0].IsExact  ? MatchKind.Exact
                      : MatchKind.Close;
        ScanHistory.Insert(0, new ScanHistoryEntry(rs, topName, matchKind)
            { IsInCart = cart.Contains(topName) });
        while (ScanHistory.Count > 20) ScanHistory.RemoveAt(ScanHistory.Count - 1);
        RebuildFilteredHistory();
    }

    private HashSet<string> CartNames() =>
        ShoppingList.Select(x => x.ResourceName).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private void RebuildFilteredHistory()
    {
        FilteredScanHistory.Clear();
        foreach (var e in ScanHistory.Where(PassesHistoryFilter))
            FilteredScanHistory.Add(e);
    }

    private bool PassesHistoryFilter(ScanHistoryEntry e) => HistoryFilter switch
    {
        HistoryFilter.Exact         => e.Match == MatchKind.Exact,
        HistoryFilter.ExactAndClose => e.Match is MatchKind.Exact or MatchKind.Close,
        _                           => true,
    };

    private void RefreshCartStatus()
    {
        var cart = CartNames();

        var results = ScanResults.Select(r => r with { IsInCart = cart.Contains(r.Resource.Name) }).ToList();
        ScanResults.Clear();
        foreach (var r in results) ScanResults.Add(r);

        NotifyScanDerived();

        var history = ScanHistory.Select(e => e with { IsInCart = cart.Contains(e.TopResource) }).ToList();
        ScanHistory.Clear();
        foreach (var e in history) ScanHistory.Add(e);
        RebuildFilteredHistory();
    }

    [RelayCommand]
    private void ClearScan()
    {
        RsInput = "";
        ScanResults.Clear();
        StatusText = "";
        NotifyScanDerived();
    }

    [RelayCommand]
    private void ClearHistory()
    {
        ScanHistory.Clear();
        RebuildFilteredHistory();
    }

    [RelayCommand]
    private void ToggleScan()
    {
        if (IsScanActive)
        {
            _scanner.Stop();
            IsScanActive = false;
            ScanStatusText = "● idle";
        }
        else
        {
            _scanner.Start();
            IsScanActive = true;
        }
    }

    public void SetScanRegion(ScanRegion r) => _scanner.SetScanRegion(r.X, r.Y, r.Width, r.Height);

    private bool _pausedByHide;

    public void PauseScanner()
    {
        if (!IsScanActive) return;
        _scanner.Stop();
        IsScanActive = false;
        ScanStatusText = "● idle";
        _pausedByHide = true;
    }

    public void ResumeScanner()
    {
        if (!_pausedByHide) return;
        _pausedByHide = false;
        _scanner.Start();
        IsScanActive = true;
    }

    public void StopScanner() => _scanner.Dispose();

    private int _spinIdx;
    private static readonly string[] _spinFrames = ["|", "/", "-", "\\"];
    private DateTime _lastValueTime = DateTime.MinValue;

    private void OnOcrValue(int value)
    {
        _lastValueTime = DateTime.Now;
        RsInput = value.ToString();
        ScanStatusText = $"● {value:N0}";
        Lookup();
        OcrValueReceived?.Invoke(value);
    }

    private void OnScanTick()
    {
        if ((DateTime.Now - _lastValueTime).TotalSeconds > 3)
        {
            _spinIdx = (_spinIdx + 1) % _spinFrames.Length;
            ScanStatusText = $"{_spinFrames[_spinIdx]} scanning";
        }
    }

    // ── Work Orders ──────────────────────────────────────────────────────────

    [RelayCommand]
    private void SaveWorkOrder(WorkOrder wo)
    {
        App.Data.SaveWorkOrder(wo);
        LoadWorkOrders();
    }

    [RelayCommand]
    private void DeleteWorkOrder(string id)
    {
        App.Data.DeleteWorkOrder(id);
        var existing = WorkOrders.FirstOrDefault(w => w.Id == id);
        if (existing != null) WorkOrders.Remove(existing);
    }

    // ── Shopping List ────────────────────────────────────────────────────────

    [RelayCommand]
    private void AddResourceToShopping(Resource r)
    {
        App.Data.AddToShoppingList(r.Name, 1, "SCU");
        LoadShoppingList();
    }

    [RelayCommand]
    private void AddToShopping(BlueprintIngredient ing)
    {
        App.Data.AddToShoppingList(ing.ResourceName, ing.Quantity, ing.Unit);
        LoadShoppingList();
    }

    [RelayCommand]
    private void RemoveFromShopping(string resourceName)
    {
        App.Data.RemoveFromShoppingList(resourceName);
        var item = ShoppingList.FirstOrDefault(s => s.ResourceName == resourceName);
        if (item != null) ShoppingList.Remove(item);
    }

    [RelayCommand]
    private void ClearShopping()
    {
        App.Data.ClearShoppingList();
        ShoppingList.Clear();
    }

    // ── Blueprints ───────────────────────────────────────────────────────────

    [RelayCommand]
    private void SearchBlueprints()
    {
        var results = App.Data.SearchBlueprints(BlueprintSearch);
        BlueprintResults.Clear();
        foreach (var bp in results) BlueprintResults.Add(bp);
    }

    // ── Pinning ──────────────────────────────────────────────────────────────

    [RelayCommand]
    private void TogglePin(Resource r)
    {
        r.IsPinned = !r.IsPinned;
        App.Data.SetPinned(r.Name, r.IsPinned);
    }
}

public record MatchResult(Resource Resource, int InputRs, int Nodes, bool IsExact, double ErrorPct)
{
    public string BadgeText => IsExact ? "EXACT" : $"~{ErrorPct:0.00}%";
    public string NodesText => Nodes == 1 ? "×1 node" : $"×{Nodes} nodes";
    public string CardColor => IsExact ? "#3FB950" : "#E3B341";
    public bool IsInCart { get; set; }

    public RefineryYield? BestRefinery =>
        Resource.Refineries.Count == 0 ? null : Resource.Refineries.OrderByDescending(x => x.ModifierPct).First();
    public string RefineryText
    {
        get
        {
            var b = BestRefinery;
            if (b == null || b.ModifierPct == 0) return "No refinery modifier";
            return $"Refinery  {(b.ModifierPct > 0 ? "+" : "")}{b.ModifierPct}% at {b.Station.Split(' ')[0]}";
        }
    }
    public string RefineryColor
    {
        get
        {
            var b = BestRefinery;
            if (b == null || b.ModifierPct == 0) return "#8B949E";
            return b.ModifierPct > 0 ? "#3FB950" : "#EF4444";
        }
    }
}

public enum MatchKind { None, Close, Exact }

public enum HistoryFilter { All, ExactAndClose, Exact }

public record ScanHistoryEntry(int Rs, string TopResource, MatchKind Match)
{
    public string RsColor => Match switch
    {
        MatchKind.Exact => "#FF3FB950",
        MatchKind.Close => "#FFE3B341",
        _               => "#FFFF4545",
    };
    public double NameOpacity => Match == MatchKind.None ? 0.4 : 1.0;
    public bool IsInCart { get; set; }
}
