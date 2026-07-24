using System;
using System.Collections.Generic;
using System.Globalization;

namespace NexusApp.Services;

/// <summary>
/// Pure, WPF-free presentation seam for the auto-relaunch notice (render-crash recovery banner).
/// Both surfaces (the Operations HUB strip and the Settings > Diagnostics "Last automatic restart"
/// row) share this one source of truth for the two decisions that have no UI dependency: whether
/// this process was started by CrashGuard's auto-relaunch, and how the last-relaunch timestamp
/// renders (including the empty state). Keeping it here lets the seams be unit-tested headlessly.
/// </summary>
public static class RelaunchNotice
{
    /// <summary>Shown in the Settings row value when no auto-relaunch has ever been recorded.</summary>
    public const string NoneRecorded = "None recorded";

    /// <summary>Local-time display format for the last-relaunch timestamp (calm, mono, no seconds).</summary>
    public const string TimestampFormat = "yyyy-MM-dd HH:mm";

    /// <summary>The small marker next to the timestamp naming the underlying Windows display error.</summary>
    public const string Marker = "display error (0x88980406)";

    /// <summary>
    /// True when the process arguments carry CrashGuard's auto-relaunch flag, i.e. this instance was
    /// started by the render-thread-failure recovery rather than launched by the user. Keyed on the
    /// argument (not marker freshness) so a manual restart is never mislabeled as an auto-relaunch.
    /// </summary>
    public static bool WasAutoRelaunched(IReadOnlyList<string>? args)
    {
        if (args is null) return false;
        for (int i = 0; i < args.Count; i++)
            if (args[i] == CrashGuard.RelaunchArg) return true;
        return false;
    }

    /// <summary>
    /// Renders a stored-UTC last-relaunch instant as calm local-time "yyyy-MM-dd HH:mm", or the
    /// empty-state label when nothing has been recorded. The stored value is UTC; a value handed back
    /// as Local (or Unspecified) is still normalized so the displayed local time is always correct.
    /// </summary>
    public static string FormatTimestamp(DateTime? lastUtc)
    {
        if (lastUtc is not { } when) return NoneRecorded;
        // Normalize to UTC first (a value handed back from JSON as Local or Unspecified must not be
        // taken at face value), then localize for display.
        var utc = when.Kind == DateTimeKind.Local ? when.ToUniversalTime()
                                                  : DateTime.SpecifyKind(when, DateTimeKind.Utc);
        return utc.ToLocalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture);
    }
}
