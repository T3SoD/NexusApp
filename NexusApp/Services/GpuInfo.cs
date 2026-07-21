using Microsoft.Win32;

namespace NexusApp.Services;

/// <summary>
/// Startup diagnostic: display adapter name and driver version, read from the registry
/// display-adapter class key (no WMI dependency, no device handles, no serial numbers -
/// safe for shared snapshots). The driver version is the triage pivot for render thread
/// failures (see <see cref="CrashGuard"/>).
/// </summary>
public static class GpuInfo
{
    // The display-adapter device class; instance subkeys are 0000, 0001, ...
    private const string DisplayClassKey =
        @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    public static void LogAdapters()
    {
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(DisplayClassKey);
            if (cls is null) { Logger.Info("[WIN] display adapter: unknown (registry class key missing)"); return; }
            bool any = false;
            foreach (var sub in cls.GetSubKeyNames())
            {
                if (sub.Length != 4 || !int.TryParse(sub, out _)) continue;
                using var k = cls.OpenSubKey(sub);
                var line = AdapterLine(k?.GetValue("DriverDesc") as string, k?.GetValue("DriverVersion") as string);
                if (line is null) continue;
                Logger.Info(line);
                any = true;
            }
            if (!any) Logger.Info("[WIN] display adapter: none found in registry");
        }
        catch { /* diagnostics must never throw */ }
    }

    internal static string? AdapterLine(string? description, string? driverVersion) =>
        string.IsNullOrWhiteSpace(description)
            ? null
            : $"[WIN] display adapter: {description} (driver {(string.IsNullOrWhiteSpace(driverVersion) ? "unknown" : driverVersion)})";
}
