using System.Timers;
using NexusApp.Models;

namespace NexusApp.Services;

// Polls the contract region once per second, parses the OCR text into a ContractDetails,
// deduplicates on normalized title, and raises ContractScanned on each new contract seen.
// Mirrors ScannerService's AutoReset=false + manual-rearm timer pattern exactly.
public sealed class ContractScanner : IDisposable
{
    private readonly ContractOcrService _ocr;
    private System.Timers.Timer? _timer;
    private bool _running;
    private bool _busy;
    private string _lastKey = "";
    private string _lastDiag = "";   // dedup for the per-stage scan breadcrumb

    public event Action<ContractDetails>? ContractScanned;

    public bool IsRunning => _running;

    public ContractScanner(ContractOcrService ocr)
    {
        _ocr = ocr;
    }

    public void Start()
    {
        if (_running) return;
        _running = true;
        Logger.Info("[CONTRACT] contract scanner started (polling @1000ms)");
        _timer = new System.Timers.Timer(1000);
        _timer.Elapsed += OnTick;
        _timer.AutoReset = false;
        _timer.Start();
    }

    public void Stop()
    {
        if (_running) Logger.Info("[CONTRACT] contract scanner stopped");
        _running = false;
        _timer?.Stop();
        _timer?.Dispose();
        _timer = null;
    }

    private async void OnTick(object? sender, ElapsedEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var text = await _ocr.ScanRegionTextAsync();
            var d = text is null ? null : ContractParser.Parse(text);

            // Per-stage diagnostic (deduped) so a blank Hauling card can be traced to the failing
            // step: no region, empty capture, text-but-not-a-contract, or parsed OK. Contract panels
            // carry no player identity, and the snippet is truncated, so this is PII-safe to log.
            LogScanDiagnostic(text, d);

            if (d != null)
            {
                var key = ContractParser.NormalizeTitle(d.Title);
                if (key.Length > 0 && key != _lastKey)
                {
                    _lastKey = key;
                    Logger.Info($"[CONTRACT] scanned: {d.ContractedBy} reward {d.Reward}");
                    ContractScanned?.Invoke(d);
                }
            }
        }
        finally
        {
            _busy = false;
            if (_running)
            {
                _timer = new System.Timers.Timer(1000);
                _timer.Elapsed += OnTick;
                _timer.AutoReset = false;
                _timer.Start();
            }
        }
    }

    // Emits one [CONTRACT] breadcrumb per distinct scan outcome, so the App Log Monitor shows exactly
    // which step is failing when the Hauling card stays bare. Deduped so a static panel doesn't spam.
    private void LogScanDiagnostic(string? text, ContractDetails? d)
    {
        string state, detail;
        if (!_ocr.IsAvailable)                    { state = "unavail";  detail = "ocr-unavailable"; }
        else if (!_ocr.HasRegion)                 { state = "noregion"; detail = "no region set (draw the yellow contract box)"; }
        else if (string.IsNullOrWhiteSpace(text)) { state = "notext";   detail = "region set but capture/OCR returned no text"; }
        else if (d is null)                       { state = "noanchor"; detail = $"text but no contract anchor: {Snippet(text)}"; }
        else { state  = "parsed:" + ContractParser.NormalizeTitle(d.ContractedBy);
               detail = $"parsed contractor '{d.ContractedBy}' reward {d.Reward} objectives {d.Objectives.Count}"; }

        // Dedup on the COARSE state (not the noisy per-frame text) so a stable panel logs one line per
        // state change, not one per tick - otherwise OCR jitter floods nexus.log past its rotation cap.
        if (state == _lastDiag) return;
        _lastDiag = state;
        Logger.Info($"[CONTRACT] scan: {detail}");
    }

    private static string Snippet(string s)
    {
        var oneLine = s.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return oneLine.Length > 100 ? oneLine[..100] : oneLine;
    }

    public void Dispose()
    {
        Stop();
    }
}
