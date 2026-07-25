using System.Diagnostics;
using System.IO;
using System.Text.Json;
using NexusApp.Models;

namespace NexusApp.Services;

// Seeds and launches the isolated demo profile (--demo-profile): a disposable
// %AppData%\NexusApp_demo root filled from the embedded StarlightHauler dataset, used for
// public screenshots. Nothing here ever reads or writes the live profile.
public static class DemoProfile
{
    // The embedded dataset (NexusApp/Data/demo/**). Extracted verbatim except settings.json,
    // whose GameLogPath is patched to the seeded Game.log once the root is known.
    internal static readonly string[] Files = ["settings.json", "nexus.db", "network.db", "Game.log"];

    private const string ResourcePrefix = "NexusApp.Data.demo.";

    public static bool IsSeeded(string root) => File.Exists(Path.Combine(root, "settings.json"));

    // Idempotent: a root that already has a settings.json is left untouched so demo-session
    // state (window layout, tab positions) survives relaunches. Reset() starts fresh.
    public static void EnsureSeeded(string root)
    {
        if (IsSeeded(root)) return;
        Logger.Info("[WIN] demo mode: seeding demo profile");
        Directory.CreateDirectory(root);
        var asm = typeof(DemoProfile).Assembly;
        // settings.json doubles as the seeded marker (IsSeeded), so it is extracted LAST: a
        // failure part-way through can never leave a root that looks seeded but is missing
        // databases (such a root would self-hide forever behind the early return above).
        foreach (var name in Files.OrderBy(n => n == "settings.json" ? 1 : 0))
        {
            using var src = asm.GetManifestResourceStream(ResourcePrefix + name)
                ?? throw new FileNotFoundException($"embedded demo resource missing: {name}");
            using var dst = File.Create(Path.Combine(root, name));
            src.CopyTo(dst);
        }
        PatchGameLogPath(root);
    }

    // The embedded settings.json ships with GameLogPath = "" (absolute machine paths must
    // never be embedded); point it at the seeded demo Game.log now that the root is known.
    private static void PatchGameLogPath(string root)
    {
        var path = Path.Combine(root, "settings.json");
        var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new AppSettings();
        s.GameLogPath = Path.Combine(root, "Game.log");
        File.WriteAllText(path, JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static void Reset(string root)
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        Logger.Info("[UI] admin: demo profile reset");
    }

    // A demo instance must never fall back to probing the REAL Star Citizen install: if the
    // seeded settings are missing or unseeded (GameLogPath empty), pin the watcher to the demo
    // root's Game.log even when that file does not exist yet (PreferredPath honors missing
    // files instead of probing).
    internal static string PinGameLogPath(string? current, string root) =>
        string.IsNullOrEmpty(current) ? Path.Combine(root, "Game.log") : current;

    // Seed if needed, then start the demo instance. Returns false (and logs) when the child
    // could not start; the caller must keep the live app running in that case. ProcessPath,
    // not Assembly.Location: the latter is empty under the single-file portable publish.
    public static bool StartDemoInstance()
    {
        try
        {
            EnsureSeeded(AppPaths.DemoRoot);
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
            {
                Logger.Error("[WIN] demo mode: process path unavailable; not launching");
                return false;
            }
            Logger.Info("[WIN] demo mode: launching demo instance");
            Process.Start(new ProcessStartInfo(exe, AppPaths.DemoArg) { UseShellExecute = false });
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error("[WIN] demo mode: failed to launch demo instance", ex);
            return false;
        }
    }
}
