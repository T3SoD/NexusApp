using System;
using System.Windows;
using System.Windows.Threading;

namespace NexusApp.Views;

/// <summary>Which control a tour step explains. The host resolves each to a live element.</summary>
public enum TutorialTarget
{
    None,            // centered step, no anchor
    SessionPill,     // GAME SESSION header pill
    BlueprintsPill,  // BLUEPRINTS header pill
    AppDock,         // the whole Wrist-OS module dock
    OperationsKpis,  // KPI card row on the Operations page
    RsDecoderTile,   // dock tile (ring only, no navigation)
    RefineryTile,    // dock tile (ring only, no navigation)
    HaulingTile,     // dock tile (ring only, no navigation)
    NetworkTile,     // dock tile (ring only, no navigation)
    OpenOverlay,     // overlay toggle button in the header
    OverlayHub,      // overlay HUB tab status lights
    ScanToggle,      // overlay SCAN tab Auto-scan RS switch
    ContractRegion,  // overlay HAULING tab set-contract-region link
}

/// <summary>
/// Drives the welcome tour: a lean, ordered set of steps, each shown as an anchored
/// <see cref="CoachMarkWindow"/> bubble beside the control a <see cref="HighlightWindow"/>
/// ring points at. The host supplies a resolver that navigates to and returns the
/// target element for each step, and an action to launch auto-scan region setup at
/// the end. Modeless - the app stays live behind the tour.
/// </summary>
public sealed class TourController
{
    internal sealed record Step(TutorialTarget Target, string Title, string Caption);

    internal static readonly Step[] Steps =
    [
        new(TutorialTarget.None, "Already working",
            "Nexus went live the moment you launched it - your game feeds it everything through one log file, no setup required. This quick tour shows you the map: a status strip that reports, a module dock that works, and an overlay that flies with you."),
        new(TutorialTarget.SessionPill, "The status strip",
            "This pill lights up when Star Citizen writes to Game.log and Nexus reads along - read-only, nothing modified, EAC-safe. If it stays dark, point Settings at your Game.log path."),
        new(TutorialTarget.BlueprintsPill, "Blueprints count themselves",
            "Pick up a blueprint in the verse and this count ticks on its own - every find lands in your Blueprint Library with zero clicks. WHERE TO MINE there shows how to get the ones you are missing."),
        new(TutorialTarget.AppDock, "The module dock",
            "Eight modules, one click each. When you want to do something instead of glance at it, it lives on this dock - Settings included, down at the bottom."),
        new(TutorialTarget.OperationsKpis, "Operations preflight",
            "Nexus lands here on launch. Read the row - last scan, refinery queue, cargo in transit, session blueprints, network coverage - and every panel below links straight into its module."),
        new(TutorialTarget.RsDecoderTile, "RS Decoder",
            "Your first stop after scanning a rock: click in, type the RS value, and get a best-match ore breakdown. Or skip the typing - auto-scan can read it off your screen."),
        new(TutorialTarget.RefineryTile, "Refinery",
            "Click + Add work order inside and Nexus counts down every job. The pill on this tile shows how many orders are cooking."),
        new(TutorialTarget.HaulingTile, "Hauling tracks itself",
            "Accept a hauling contract in game and it appears here on its own - the pill shows your active hauls. Click in for consolidated collect and deliver stops across all of them."),
        new(TutorialTarget.NetworkTile, "Share without a server",
            "Network trades blueprint libraries by file - export yours, import a friend's, see who owns what together. Fully offline; nothing leaves your machine unless you hand it over."),
        new(TutorialTarget.OpenOverlay, "The third space",
            "Click here to launch the overlay - a compact panel that floats over Star Citizen so you never leave the game to use Nexus."),
        new(TutorialTarget.OverlayHub, "Proof of life",
            "The overlay opens on the HUB: green lights mean a feed is live right now - session, RS auto-scan, contracts. Blueprints collected, server, and shard read out below."),
        new(TutorialTarget.ScanToggle, "Auto-scan is opt-in",
            "Switch this on and Nexus reads rock signatures straight off your screen through the magenta box you draw once. It pauses on its own whenever you and the game are both in the background."),
        new(TutorialTarget.ContractRegion, "Contracts get their own box",
            "Contract scanning uses a separate region, set right here - yellow for contracts, magenta stays RS. The two never interfere."),
        new(TutorialTarget.None, "You have the map",
            "Strip reports, dock works, overlay flies with you - and the loop is track, scan, refine, haul, share. One thing left: click Set up auto-scan to draw your magenta RS box now, or replay this tour anytime from Help."),
    ];

    private readonly Window _owner;
    private readonly Func<TutorialTarget, FrameworkElement?> _resolve;
    private readonly Action _onSetupAutoScan;

    private HighlightWindow? _ring;
    private CoachMarkWindow? _bubble;
    private int _i;

    public TourController(Window owner, Func<TutorialTarget, FrameworkElement?> resolve, Action onSetupAutoScan)
    {
        _owner = owner;
        _resolve = resolve;
        _onSetupAutoScan = onSetupAutoScan;
    }

    public void Start()
    {
        _i = 0;
        _ring = new HighlightWindow();
        _bubble = new CoachMarkWindow();
        _bubble.BackClicked += () => Go(-1);
        _bubble.NextClicked += OnNext;
        _bubble.SkipClicked += () => Finish(setupAutoScan: false);
        Render();
    }

    private void OnNext()
    {
        if (_i == Steps.Length - 1) Finish(setupAutoScan: true);
        else Go(+1);
    }

    private void Go(int delta)
    {
        _i = Math.Clamp(_i + delta, 0, Steps.Length - 1);
        Render();
    }

    private void Render()
    {
        var step = Steps[_i];
        bool isLast = _i == Steps.Length - 1;
        var target = _resolve(step.Target);   // navigates (page/overlay) and returns the element, or null

        _bubble!.SetContent(step.Title, step.Caption, _i, Steps.Length,
                            isFirst: _i == 0, isLast: isLast,
                            nextLabel: isLast ? "Set up auto-scan" : "Next");

        if (target != null)
        {
            // Defer one layout pass so a just-switched page or just-opened overlay has settled.
            var captured = target;
            _owner.Dispatcher.BeginInvoke(new Action(() =>
            {
                _ring!.HighlightControl(captured);
                _bubble!.ShowBeside(captured);
                _bubble!.BringToTop();
            }), DispatcherPriority.Loaded);
        }
        else
        {
            _ring!.HideRing();
            _bubble!.ShowCentered(_owner);
        }
    }

    private void Finish(bool setupAutoScan)
    {
        _ring?.Close(); _ring = null;
        _bubble?.Close(); _bubble = null;
        if (setupAutoScan) _onSetupAutoScan();
    }
}
