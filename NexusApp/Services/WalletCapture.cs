namespace NexusApp.Services;

// The OCR wallet burst machine (spec section 3): one mobiGlas-open trigger line starts one
// self-terminating burst; nothing polls and no close event exists or is needed. Every seam
// (capture, gate, clock, delay) is injectable so the machine tests headless. The capture
// instant is expressed in the Game.log clock domain: the trigger line's own stamp plus locally
// measured elapsed time, so anchor math never mixes clocks with the ledger.
public sealed class WalletCapture : IDisposable
{
    public static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(500);
    public static readonly TimeSpan GrabSpacing = TimeSpan.FromMilliseconds(500);
    public static readonly TimeSpan BurstBudget = TimeSpan.FromSeconds(5);
    public const int MaxGrabs = 3;

    private readonly Func<Task<string?>> _capture;
    private readonly Func<bool> _canCapture;
    private readonly Func<DateTime> _utcNow;
    private readonly Func<TimeSpan, Task> _delay;
    private readonly GameLogSubscription? _sub;
    private int _busy;

    // (balance, triggerUtc, captureUtc), both stamps in the Game.log clock domain.
    public event Action<long, DateTime, DateTime>? BalanceCaptured;

    // "confirmed" | "timeout" | "aborted" | "busy" | "gated"; null until the first trigger.
    public string? LastOutcome { get; private set; }

    public WalletCapture(GameLogFeed? feed = null, Func<Task<string?>>? capture = null,
                         Func<bool>? canCapture = null, Func<DateTime>? utcNow = null,
                         Func<TimeSpan, Task>? delay = null)
    {
        _capture = capture ?? (() => Task.FromResult<string?>(null));
        _canCapture = canCapture ?? (() => false);
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _delay = delay ?? (ts => Task.Delay(ts));
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
                    if (seen.Contains(value.Value))
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
                    await _delay(GrabSpacing).ConfigureAwait(false);
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
