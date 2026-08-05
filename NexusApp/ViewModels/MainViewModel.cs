using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NexusApp.Models;
using NexusApp.Services;

namespace NexusApp.ViewModels;

// Tri-state for auto-scan status indicators: Off, On (running), or Paused (the user wants it on but it
// is suspended because neither Nexus nor Star Citizen is in the foreground).
public enum ScanIndicator { Off, On, Paused }

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
    // ScanResults through the active filter pill (issue #34: the pills govern live results too).
    // A live collection, not a LINQ view: the overlay assigns it as an ItemsSource imperatively,
    // and cart-toggle rebuilds must keep flowing through CollectionChanged.
    public ObservableCollection<MatchResult> FilteredScanResults { get; } = [];
    public ObservableCollection<ScanHistoryEntry> ScanHistory { get; } = [];
    public ObservableCollection<ScanHistoryEntry> FilteredScanHistory { get; } = [];
    public ObservableCollection<Resource> AllResources { get; } = [];
    public ObservableCollection<WorkOrder> WorkOrders { get; } = [];
    public ObservableCollection<ShoppingItem> ShoppingList { get; } = [];
    public ObservableCollection<Blueprint> BlueprintResults { get; } = [];

    // ── RS Decoder (C1 dashboard) derived views ───────────────────────────────
    // All derived from the FILTERED results: under the Exact pill a close-only scan shows the
    // empty state, not a blank hero (issue #34).
    public MatchResult? BestMatch => FilteredScanResults.Count > 0 ? FilteredScanResults[0] : null;
    public IEnumerable<MatchResult> OtherMatches => FilteredScanResults.Skip(1);
    public bool HasResults => FilteredScanResults.Count > 0;
    public bool NoResults  => FilteredScanResults.Count == 0;
    // The scan DID match, but the active pill hides everything (Exact over a close-only scan).
    // Both surfaces must say so, or a successful scan reads as a dead pipeline (issue #34).
    public bool ResultsHiddenByFilter => ScanResults.Count > 0 && FilteredScanResults.Count == 0;
    public bool IsScanning => _scanner.IsRunning;

    private void NotifyScanDerived()
    {
        OnPropertyChanged(nameof(BestMatch));
        OnPropertyChanged(nameof(OtherMatches));
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(NoResults));
        OnPropertyChanged(nameof(ResultsHiddenByFilter));
    }

    public event Action<int>? OcrValueReceived;
    public event Action<ScanPhase>? OcrPhaseReceived;
    public event Action<int>? OcrProgressReceived;

    // True only while a filter pill click rebuilds the derived results. The rebuild raises
    // BestMatch synchronously, so the view reads this to sync its motion tracker instead of
    // replaying the full scan choreography for an unchanged scan (issue #34).
    public bool ResultsRebuildIsFilterFlip { get; private set; }

    partial void OnHistoryFilterChanged(HistoryFilter value)
    {
        RebuildFilteredHistory();
        ResultsRebuildIsFilterFlip = true;
        try { RebuildFilteredResults(); }
        finally { ResultsRebuildIsFilterFlip = false; }
    }

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

        // Auto-scan is OPT-IN - deliberately NOT started here. Continuously capturing the screen
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
        catch { /* schema mismatch on old DB - migration handles it on next launch */ }
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
        RebuildFilteredResults();

        if (!addToHistory || !isNewScan) return;

        var (topName, matchKind) = ScanClassification.Summarize(matches);
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

    // Re-derive FilteredScanResults from ScanResults; every ScanResults mutation site calls this.
    private void RebuildFilteredResults()
    {
        FilteredScanResults.Clear();
        foreach (var m in ScanResultsFilter.Apply(ScanResults, HistoryFilter))
            FilteredScanResults.Add(m);
        NotifyScanDerived();
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

        RebuildFilteredResults();

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
        RebuildFilteredResults();
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

    // Scanning can be suspended for more than one reason at once: the overlay being hidden, and the app
    // and game both being in the background. The user's intent is remembered so scanning resumes only
    // when EVERY reason has cleared - one reason lifting must not override another that still holds.
    private bool _pausedByHide;
    private bool _pausedByBackground;
    private bool _scanIntent;   // the user had scanning on when it was paused

    public void PauseScanner()        => Pause(ref _pausedByHide);
    public void ResumeScanner()       => Resume(ref _pausedByHide);
    public void PauseForBackground()  => Pause(ref _pausedByBackground);
    public void ResumeForBackground() => Resume(ref _pausedByBackground);

    private void Pause(ref bool reason)
    {
        // Set the reason BEFORE flipping IsScanActive so listeners that re-read scan state on the
        // IsScanActive change (e.g. the overlay LEDs via RsScanState) already see this pause reason.
        reason = true;
        if (IsScanActive)
        {
            _scanIntent = true;
            _scanner.Stop();
            IsScanActive = false;
            ScanStatusText = "● paused";
        }
    }

    private void Resume(ref bool reason)
    {
        reason = false;
        if (_pausedByHide || _pausedByBackground) return;   // another reason still holds it paused
        if (_scanIntent) { _scanIntent = false; _scanner.Start(); IsScanActive = true; }
    }

    // RS auto-scan status for the HUB / header indicators: On while running, Paused when the user has it
    // on but it is suspended because neither Nexus nor Star Citizen is in front, else Off.
    public ScanIndicator RsScanState =>
        IsScanActive                          ? ScanIndicator.On
        : (_scanIntent && _pausedByBackground) ? ScanIndicator.Paused
        : ScanIndicator.Off;

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
        if (r.Method == "salvage") return;   // synthetic decode entry, not a purchasable ore
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

    // Cart button on the overlay's scan result cards: one command that flips between
    // add/remove depending on current cart membership, so the XAML only needs a single
    // Command binding. Both branches mutate ShoppingList, which drives RefreshCartStatus
    // and thus IsInCart on the next CollectionChanged pass - no extra INPC needed here.
    [RelayCommand]
    private void ToggleCart(MatchResult m)
    {
        if (m.IsSalvage) return;   // the button is hidden on salvage cards; guard the command too
        if (m.IsInCart) RemoveFromShopping(m.Resource.Name);
        else AddResourceToShopping(m.Resource);
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
    // Synthetic salvage decode (SalvageDecode, issue #34): counts panels, not rock nodes, and
    // carries none of the ore affordances (cart, refinery, composition).
    public bool IsSalvage => Resource.Method == "salvage";
    public bool CanCart => !IsSalvage;
    public string BadgeText => IsExact ? "EXACT" : $"~{ErrorPct:0.00}%";
    public string NodesText => IsSalvage
        ? (Nodes == 1 ? "×1 panel" : $"×{Nodes} panels")
        : (Nodes == 1 ? "×1 node" : $"×{Nodes} nodes");
    public string CardColor => IsExact ? "#3FB950" : "#E3B341";
    public bool IsInCart { get; set; }

    // Same picker the Codex dossier uses (app review G10). It has to be the same one: two surfaces
    // naming a different "best refinery" for the same ore would be worse than either being wrong.
    // Modifier still decides; the player's position only settles ties.
    // (RefineryText/RefineryColor, which used to sit here, were removed in the F14 pass: the pill
    // inventory's repo-wide grep found ZERO bindings to either - dead code that never rendered.)
    public RefineryYield? BestRefinery =>
        NexusApp.Services.Map.RefineryPlaces.Best(Resource.Refineries, App.Map, App.Player.Current);
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
