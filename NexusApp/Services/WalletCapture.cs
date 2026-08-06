namespace NexusApp.Services;

// The OCR wallet burst machine (spec section 3): one mobiGlas-open trigger line starts one
// self-terminating burst; nothing polls and no close event exists or is needed. Every seam
// (capture, gate, clock, delay) is injectable so the machine tests headless. The capture
// instant is expressed in the Game.log clock domain: the trigger line's own stamp plus locally
// measured elapsed time, so anchor math never mixes clocks with the ledger.
public sealed class WalletCapture : IDisposable
{
    // Cadence tightened 2026-08-06: confirm can land ~550 ms after the
    // trigger line, or at settle+one-grab when the read matches the current estimate. Empty
    // grabs retry fast (the mobiGlas boot animation resolves in fractions of a spacing), and
    // six grabs keep the burst spanning a slow boot inside the same budget.
    public static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan GrabSpacing = TimeSpan.FromMilliseconds(300);
    public static readonly TimeSpan RetrySpacing = TimeSpan.FromMilliseconds(150);
    public static readonly TimeSpan BurstBudget = TimeSpan.FromSeconds(5);
    // A single dual-recognition-vetted read this close to the estimate confirms alone (live
    // 17:01: a correct read waited 3 s for a partner over a 240 aUEC drift). Larger moves keep
    // the two-read rule; a surviving misread inside the band costs at most a small row that the
    // next capture corrects.
    public const long SingleReadTolerance = 100_000;
    // High enough that the TIME budget is the real cap: a cold mobiGlas boot renders the
    // balance over a second in, and the first readable grab needs partners after it
    // (live evidence, 2026-08-06 16:15).
    public const int MaxGrabs = 12;

    private readonly Func<Task<string?>> _capture;
    private readonly Func<bool> _canCapture;
    private readonly Func<DateTime> _utcNow;
    private readonly Func<TimeSpan, Task> _delay;
    private readonly Func<long?> _currentEstimate;
    private readonly GameLogSubscription? _sub;
    private int _busy;

    // (balance, triggerUtc, captureUtc), both stamps in the Game.log clock domain.
    public event Action<long, DateTime, DateTime>? BalanceCaptured;

    // "confirmed" | "timeout" | "aborted" | "busy" | "gated"; null until the first trigger.
    public string? LastOutcome { get; private set; }

    public WalletCapture(GameLogFeed? feed = null, Func<Task<string?>>? capture = null,
                         Func<bool>? canCapture = null, Func<DateTime>? utcNow = null,
                         Func<TimeSpan, Task>? delay = null, Func<long?>? currentEstimate = null)
    {
        _capture = capture ?? (() => Task.FromResult<string?>(null));
        _canCapture = canCapture ?? (() => false);
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _delay = delay ?? (ts => Task.Delay(ts));
        _currentEstimate = currentEstimate ?? (() => null);
        if (feed != null)
        {
            _sub = feed.Subscribe(OnLine, includeReplay: false);
        }
    }

    // Headless entry point; the subscription feeds it live lines.
    public void OnLine(GameLogEntry e)
    {
        if (e.IsReplay) return; // a replayed trigger's screen state is long gone
        if (!WalletOcrTrigger.IsMobiGlasOpenSignal(e.Raw)) return;
        if (!WalletOcrTrigger.TryParseLineUtc(e.Raw, out var triggerUtc)) return;
        if (!_canCapture())
        {
            LastOutcome = "gated";
            return;
        }
        if (System.Threading.Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            LastOutcome = "busy";
            return;
        }
        _ = RunBurstAsync(triggerUtc);
    }

    private async Task RunBurstAsync(DateTime triggerUtc)
    {
        try
        {
            Logger.Info("[WALLET] mobiGlas trigger seen, burst starting");
            var start = _utcNow();
            await _delay(SettleDelay).ConfigureAwait(false);

            var seen = new List<long>();
            for (var grab = 1; grab <= MaxGrabs; grab++)
            {
                if (_utcNow() - start > BurstBudget)
                {
                    Finish("timeout");
                    return;
                }
                if (!_canCapture())
                {
                    Finish("aborted");
                    return;
                }

                string? text;
                try
                {
                    text = await _capture().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Logger.Error($"[WALLET] grab {grab} failed ({ex.GetType().Name})");
                    text = null;
                }

                var value = text is null ? null : WalletOcrTrigger.ExtractBalance(text);
                // Raw text goes to nexus.log on purpose: this IS the live-calibration record
                // (spec section 7). Values never reach the diagnostic snapshot.
                Logger.Info($"[WALLET] grab {grab}: \"{Clean(text)}\" -> {(value?.ToString() ?? "none")}");

                if (value is not null)
                {
                    // A read inside the tolerance band of the current estimate needs no second
                    // opinion: it confirms what the tracker already believes, give or take the
                    // small drift fees cause. Otherwise the usual rule: the same value seen
                    // twice inside the burst.
                    var estimate = _currentEstimate();
                    var withinBand = estimate is { } e && Math.Abs(value.Value - e) <= SingleReadTolerance;
                    if (withinBand || seen.Contains(value.Value))
                    {
                        var captureUtc = triggerUtc + (_utcNow() - start);
                        Finish("confirmed");
                        BalanceCaptured?.Invoke(value.Value, triggerUtc, captureUtc);
                        return;
                    }
                    seen.Add(value.Value);
                }

                if (grab < MaxGrabs)
                {
                    await _delay(value is null ? RetrySpacing : GrabSpacing).ConfigureAwait(false);
                }
            }
            Finish("timeout");
        }
        catch (Exception ex)
        {
            Logger.Error($"[WALLET] burst crashed ({ex.GetType().Name})");
            LastOutcome = "timeout";
        }
        finally
        {
            System.Threading.Interlocked.Exchange(ref _busy, 0);
        }
    }

    private void Finish(string outcome)
    {
        LastOutcome = outcome;
        Logger.Info($"[WALLET] burst {outcome}");
    }

    private static string Clean(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var flat = text.Replace('\r', ' ').Replace('\n', ' ');
        return flat.Length <= 60 ? flat : flat.Substring(0, 60);
    }

    public void Dispose() => _sub?.Dispose();
}
