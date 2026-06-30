using System.Windows.Media.Animation;

namespace NexusApp.Views;

/// <summary>
/// The app's shared motion vocabulary: the named durations and easing curves the
/// MOBIGLAS mocks are authored with, so every animation pulls from one source of
/// truth instead of hand-coding curves. The XAML-side mirror of these easings lives
/// in Themes/GameTheme.xaml (EaseSettle / EaseReveal / EaseBreathe / EaseSlideOut).
///
/// Motion principles (from the mocks): entrances decelerate hard and "settle",
/// page reveals use the reveal bezier, ambient loops and status dots breathe on
/// easeInOut, and UI slides decelerate (cubic ease-out).
/// </summary>
public static class Motion
{
    // Durations (milliseconds).
    public const double HoverMs    = 160;   // hover-state slides (toggle knob, dock tile)
    public const double SlideMs    = 280;   // panel / dock-selector slides
    public const double PageFadeMs = 260;   // page-in opacity
    public const double PageRiseMs = 340;   // page-in rise (mock reveal is 0.34s)
    public const double CountUpMs  = 900;   // number roll-ups
    public const double BreatheMs  = 1900;  // status-dot breathe (mock live dots ~1.8-2.0s)

    // Shared easing curves. Frozen so a single instance is reused across animations.
    public static readonly IEasingFunction Settle   = Frozen(new CubicBezierEase(0.16, 0.8, 0.3, 1.0));
    public static readonly IEasingFunction Reveal   = Frozen(new CubicBezierEase(0.2, 0.8, 0.2, 1.0));
    public static readonly IEasingFunction Breathe  = Frozen(new SineEase { EasingMode = EasingMode.EaseInOut });
    public static readonly IEasingFunction SlideOut = Frozen(new CubicEase { EasingMode = EasingMode.EaseOut });

    private static IEasingFunction Frozen(EasingFunctionBase e) { e.Freeze(); return e; }

    /// <summary>
    /// When true the app minimizes motion (the "Reduce animations" setting): animation
    /// helpers snap to their final state and ambient glyphs build static. Set once at
    /// startup from settings and updated live when the Settings toggle changes.
    /// </summary>
    public static bool Reduced { get; set; }
}
