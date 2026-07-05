using System.IO;
using System.Text.Json;

namespace NexusApp.Services.Cargo;

// File helpers for the local JSON stores. Writes are atomic (a crash mid-write cannot truncate the
// live file) and loads are corruption-safe (a bad file is set aside for diagnosis and the last-known
// good backup is tried, so a single bad write never silently wipes the user's data).
internal static class JsonFile
{
    // Serialize to a temp file then swap it in with File.Replace, which is atomic on NTFS and keeps
    // the prior contents as <path>.bak.
    public static void AtomicWrite(string path, string contents)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, contents);
        if (File.Exists(path)) File.Replace(tmp, path, path + ".bak");
        else File.Move(tmp, path);
    }

    // Deserialize T from <path>. On a parse/read failure, set the primary aside as
    // <path>.corrupt-<stamp> and fall back to <path>.bak. Returns empty() if nothing is recoverable.
    public static T LoadOrRecover<T>(string path, Func<T> empty, string label) where T : class
    {
        if (!File.Exists(path)) return empty();
        var primary = TryRead<T>(path);
        if (primary != null) return primary;

        try
        {
            File.Move(path, $"{path}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}");
            Logger.Info($"[CARGO] {label} was corrupt; set aside and attempting backup recovery");
        }
        catch (Exception ex) { Logger.Error($"Failed to set aside corrupt {label}", ex); }

        var bak = TryRead<T>(path + ".bak");
        if (bak != null) { Logger.Info($"[CARGO] recovered {label} from backup"); return bak; }
        return empty();
    }

    private static T? TryRead<T>(string path) where T : class
    {
        try { if (File.Exists(path)) return JsonSerializer.Deserialize<T>(File.ReadAllText(path)); }
        catch (Exception ex) { Logger.Error($"Failed to read {Path.GetFileName(path)}", ex); }
        return null;
    }
}
