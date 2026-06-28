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

    public void Dispose()
    {
        Stop();
    }
}
