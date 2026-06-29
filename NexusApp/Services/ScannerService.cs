using System.Timers;
using NexusApp.Models;

namespace NexusApp.Services;

public enum ScanPhase { Watching, PinFound, Decoded, NoRegion }

public class ScannerService : IDisposable
{
    private readonly OcrService _ocr = new();
    private System.Timers.Timer? _timer;
    private int _pending;
    private int _pendingCount;
    private int _lastEmitted;
    private bool _running;
    private ScanPhase _currentPhase = ScanPhase.Watching;
    private DateTime _lastBeat;   // throttles the [SCAN] capture heartbeat (see OnTick)

    public event Action<int>? ValueDetected;
    public event Action<ScanPhase>? PhaseChanged;
    public event Action<int>? CandidateProgress;
    public event Action? ScanTick;
    public event Action<string>? StatusChanged;

    public bool IsAvailable => _ocr.IsAvailable;
    public bool IsRunning    => _running;

    public void SetScanRegion(int x, int y, int w, int h) => _ocr.SetRegion(x, y, w, h);

    public void Start()
    {
        if (_running) return;
        _running = true;
        // Diagnostic breadcrumb: marks when continuous full-screen capture begins. Lets a tab-out
        // report be lined up against capture activity - confirming OR clearing the auto-scan theory.
        Logger.Info("[SCAN] auto-scan started (continuous full-screen capture @150ms)");
        StatusChanged?.Invoke(_ocr.IsAvailable ? "active" : "no OCR engine");
        _timer = new System.Timers.Timer(150);
        _timer.Elapsed += OnTick;
        _timer.AutoReset = false;
        _timer.Start();
    }

    public void Stop()
    {
        if (_running) Logger.Info("[SCAN] auto-scan stopped");
        _running = false;
        _timer?.Stop();
        _timer?.Dispose();
        _timer = null;
    }

    private async void OnTick(object? sender, ElapsedEventArgs e)
    {
        if (!_running) return;

        if (_ocr.IsAvailable)
        {
            // Sparse heartbeat (every ~10s, NOT per 150ms tick) so a tab-out timestamp can be
            // matched to "capture was active then" without flooding the log.
            var now = DateTime.UtcNow;
            if (now - _lastBeat >= TimeSpan.FromSeconds(10))
            {
                _lastBeat = now;
                Logger.Info($"[SCAN] capturing (region: {(_ocr.LastScanHadRegion ? "set" : "full-screen")})");
            }

            try
            {
                var (val, pinFound) = await _ocr.ScanFullScreenAsync();

                if (pinFound)
                {
                    if (val.HasValue)
                    {
                        if (val.Value == _pending)
                            _pendingCount++;
                        else
                        {
                            _pending = val.Value;
                            _pendingCount = 1;
                        }

                        if (_pendingCount >= 2 && val.Value != _lastEmitted)
                        {
                            _lastEmitted = val.Value;
                            App.Current.Dispatcher.Invoke(() => ValueDetected?.Invoke(val.Value));
                            EmitPhase(ScanPhase.Decoded);
                        }
                        else if (_pendingCount < 2)
                        {
                            EmitPhase(ScanPhase.PinFound);
                            App.Current.Dispatcher.Invoke(() => CandidateProgress?.Invoke(_pendingCount));
                        }
                    }
                    else
                    {
                        EmitPhase(ScanPhase.PinFound);
                    }
                }
                else
                {
                    _pending = 0;
                    _pendingCount = 0;
                    _lastEmitted = 0;
                    EmitPhase(_ocr.LastScanHadRegion ? ScanPhase.Watching : ScanPhase.NoRegion);
                }
            }
            catch { }

            App.Current.Dispatcher.Invoke(() => ScanTick?.Invoke());
        }

        if (_running)
        {
            _timer = new System.Timers.Timer(150);
            _timer.Elapsed += OnTick;
            _timer.AutoReset = false;
            _timer.Start();
        }
    }

    private void EmitPhase(ScanPhase phase)
    {
        if (_currentPhase == phase) return;
        _currentPhase = phase;
        App.Current.Dispatcher.Invoke(() => PhaseChanged?.Invoke(phase));
    }

    public void Dispose()
    {
        Stop();
        _ocr.Dispose();
    }
}
