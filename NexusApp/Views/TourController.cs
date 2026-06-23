using System;
using System.Windows;
using System.Windows.Threading;

namespace NexusApp.Views;

/// <summary>Which control a tour step explains. The host resolves each to a live element.</summary>
public enum TutorialTarget
{
    None,            // centered step, no anchor
    RsDecoder,
    OpenOverlay,
    DrawRegion,
    ScanToggle,
    OverlayTabs,
    ReferenceTools,  // the left nav rail (Blueprints / Codex / Refinery)
    BlueprintNetwork,
}

/// <summary>
/// Drives the welcome tour: a lean, ordered set of steps, each shown as an anchored
/// <see cref="CoachMarkWindow"/> bubble beside the control a <see cref="HighlightWindow"/>
/// ring points at. The host supplies a resolver that navigates to and returns the
/// target element for each step, and an action to launch auto-scan region setup at
/// the end. Modeless — the app stays live behind the tour.
/// </summary>
public sealed class TourController
{
    private sealed record Step(TutorialTarget Target, string Title, string Caption);

    private static readonly Step[] Steps =
    [
        new(TutorialTarget.None, "Welcome to Nexus",
            "Nexus reads RS values straight off your screen and decodes the resource and node count. This quick tour points out the essentials — skip anytime, replay later from Help."),
        new(TutorialTarget.RsDecoder, "RS Signal Decoder",
            "Type any RS value here and press Enter. Nexus lists every matching resource, ranked by likelihood, with its tier and where to sell."),
        new(TutorialTarget.OpenOverlay, "The floating overlay",
            "Open this to float Nexus over your game. Drag its NEXUS header to move it; it stays on top while you mine."),
        new(TutorialTarget.DrawRegion, "Auto-scan: draw the region",
            "Click here, then drag a tight box around just the RS digits on screen. Smaller is better — digits only, no labels."),
        new(TutorialTarget.ScanToggle, "Auto-scan: start reading",
            "Press start and Nexus reads your region every half-second, decoding each value automatically as you mine."),
        new(TutorialTarget.OverlayTabs, "Overlay tabs",
            "SCAN, ORDERS, and SHOPPING — your live results, refinery jobs, and shopping cart, all without leaving the game."),
        new(TutorialTarget.ReferenceTools, "Your reference tools",
            "Three tools live in the nav: the Blueprint Library for recipes, the Mining Codex reference table, and the Refinery Tracker for your refines."),
        new(TutorialTarget.BlueprintNetwork, "Blueprint Network",
            "Share your blueprint library with friends and your org — export yours, import theirs, and see who owns what together. No server, no account — you share the files yourself."),
        new(TutorialTarget.None, "You're set",
            "That's the tour. Want to set up auto-scan now? Have Star Citizen open with an RS value on screen. You can replay this anytime from Help."),
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
