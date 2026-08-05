using System.IO;
using System.Text.Json;
using NexusApp.Models;

namespace NexusApp.Services;

// Atomic persistence for ProfitHistoryState: write to .tmp, then atomic rename. Load validates
// Schema and size before parsing. Any load failure except file-not-found moves the unreadable
// file aside to <name>.bad.json first, so the next save cannot destroy the only copy.
public static class ProfitHistoryStore
{
    public const long MaxLoadBytes = 4 * 1024 * 1024;

    public static string DefaultPath => Path.Combine(AppPaths.Root, "profit_history.json");

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    // Serialize to path + ".tmp", then File.Move(tmp, path, overwrite: true). Creates the directory.
    // Returns false (and logs via Logger.Error) on failure; never throws.
    public static bool Save(string path, ProfitHistoryState state)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var tmp = path + ".tmp";
            var json = JsonSerializer.Serialize(state, SerializerOptions);
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            // NEITHER the message NOR the exception itself may reach the log: path is always
            // under %AppData%, so it would carry the Windows username into nexus.log - and .NET
            // file-IO exception Messages embed that full path, which Logger appends verbatim
            // (ex.ToString()) whenever an exception object is passed. So: operation plus
            // exception TYPE only, and no ex argument. Same rule as MarketSnapshotFile.
            Logger.Error($"Failed to save profit history ({ex.GetType().Name})");
            return false;
        }
    }

    // Null when missing, oversized, unparseable, or Schema != 1; reason describes why (for logging).
    public static ProfitHistoryState? Load(string path, out string? reason)
    {
        // reason may be logged verbatim, and path is always under %AppData% - so no reason ever
        // interpolates path, and none interpolates an exception's own Message either (a .NET
        // file-IO Message embeds the full path). Both would carry the Windows username into
        // nexus.log; the catch-all below reports the exception TYPE only. Same rule as
        // MarketSnapshotFile.
        reason = null;

        if (!File.Exists(path))
        {
            reason = "Profit history file not found";
            return null;
        }

        try
        {
            var info = new FileInfo(path);
            if (info.Length > MaxLoadBytes)
            {
                reason = $"Profit history file exceeds max size ({info.Length} > {MaxLoadBytes} bytes)";
                return PreserveUnreadable(path, ref reason);
            }

            var json = File.ReadAllText(path);
            var state = JsonSerializer.Deserialize<ProfitHistoryState>(json, SerializerOptions);

            if (state == null)
            {
                reason = "Failed to deserialize profit history";
                return PreserveUnreadable(path, ref reason);
            }

            if (state.Schema != 1)
            {
                reason = $"Unsupported profit history schema (expected 1, got {state.Schema})";
                return PreserveUnreadable(path, ref reason);
            }

            return state;
        }
        catch (Exception ex)
        {
            reason = $"Error loading profit history: {ex.GetType().Name}";
            return PreserveUnreadable(path, ref reason);
        }
    }

    // The file existed but could not be trusted: move it aside to <name>.bad.json (overwriting an
    // older one) so the next save cannot destroy the only copy. Appends the outcome to reason with
    // the same discipline as above: no path, exception TYPE only. Returns null so the failure
    // branches read as one statement.
    private static ProfitHistoryState? PreserveUnreadable(string path, ref string? reason)
    {
        try
        {
            File.Move(path, Path.ChangeExtension(path, ".bad.json"), overwrite: true);
            reason += "; unreadable file preserved aside";
        }
        catch (Exception ex)
        {
            reason += $"; preserving the unreadable file failed ({ex.GetType().Name})";
        }
        return null;
    }
}
