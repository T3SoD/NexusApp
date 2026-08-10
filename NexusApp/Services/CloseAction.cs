namespace NexusApp.Services;

/// <summary>What the main window's X button does (issue #46). Exit is the default and is what every
/// install did before this setting existed, so an upgrade changes nothing until the user asks it to.</summary>
public enum CloseBehaviour { Exit, Minimize, Tray }

/// <summary>The persisted-string seam for <see cref="CloseBehaviour"/>. AppSettings stores a string
/// so a hand-edited or future-version settings.json cannot throw on load; anything unrecognised
/// falls back to Exit, which is both the safe default and the behaviour a user who never touched
/// this setting expects.</summary>
public static class CloseAction
{
    public const string Exit = "exit";
    public const string Minimize = "minimize";
    public const string Tray = "tray";

    public static CloseBehaviour Parse(string? stored) => stored switch
    {
        Minimize => CloseBehaviour.Minimize,
        Tray => CloseBehaviour.Tray,
        _ => CloseBehaviour.Exit,
    };

    public static string ToStored(CloseBehaviour b) => b switch
    {
        CloseBehaviour.Minimize => Minimize,
        CloseBehaviour.Tray => Tray,
        _ => Exit,
    };

    /// <summary>The pill label for each behaviour, so the Settings row and any log line name it the
    /// same way.</summary>
    public static string Label(CloseBehaviour b) => b switch
    {
        CloseBehaviour.Minimize => "MINIMIZE",
        CloseBehaviour.Tray => "TRAY",
        _ => "EXIT",
    };
}
