namespace NexusApp.Services;

// Session-only impersonation for the owner's Admin tab: preview the app as a plain visitor or
// as a beta tester to verify gated UX before a release. Never persisted, so a restart always
// returns to reality. It changes only what the gates REPORT (UI visibility), never any data.
public static class GatePreview
{
    public enum Role { None, Visitor, BetaTester }

    public static Role Active { get; private set; } = Role.None;

    public static bool IsActive => Active != Role.None;

    // Raised after the role changes so MainWindow can re-gate the dock and drop cached pages
    // that captured gate state at build time. Raised on the caller's thread (Admin tab buttons,
    // so the UI thread in production).
    public static event Action? Changed;

    public static void Set(Role role)
    {
        if (Active == role) return;
        Active = role;
        Logger.Info(role == Role.None
            ? "[UI] admin: preview exited"
            : $"[UI] admin: preview role changed to {role}");
        Changed?.Invoke();
    }
}
