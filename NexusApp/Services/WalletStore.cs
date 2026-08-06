using System.IO;
using System.Text.Json;
using NexusApp.Models;

namespace NexusApp.Services;

// Atomic persistence for WalletState: write to .tmp, then atomic rename. Load validates Schema
// and size before parsing. Any load failure except file-not-found moves the unreadable file
// aside to <name>.bad.json first, so the next save cannot destroy the only copy. Same idiom and
// same logging discipline as ProfitHistoryStore.
public static class WalletStore
{
    public const long MaxLoadBytes = 1 * 1024 * 1024;

    // Enforced by WalletTracker when it appends entries, oldest dropped first.
    public const int UntrackedCap = 200;

    public static string DefaultPath => Path.Combine(AppPaths.Root, "wallet.json");

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    // Returns false (and logs via Logger.Error) on failure; never throws.
    public static bool Save(string path, WalletState state)
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
            // file-IO exception Messages embed that full path. Operation plus exception TYPE
            // only, no ex argument. Same rule as ProfitHistoryStore.
            Logger.Error($"Failed to save wallet state ({ex.GetType().Name})");
            return false;
        }
    }

    // Null when missing, oversized, unparseable, or Schema != 1; reason describes why (for
    // logging). Reasons never interpolate a path or an exception Message.
    public static WalletState? Load(string path, out string? reason)
    {
        reason = null;

        if (!File.Exists(path))
        {
            reason = "Wallet file not found";
            return null;
        }

        try
        {
            var info = new FileInfo(path);
            if (info.Length > MaxLoadBytes)
            {
                reason = $"Wallet file exceeds max size ({info.Length} > {MaxLoadBytes} bytes)";
                return PreserveUnreadable(path, ref reason);
            }

            var json = File.ReadAllText(path);
            var state = JsonSerializer.Deserialize<WalletState>(json, SerializerOptions);

            if (state == null)
            {
                reason = "Failed to deserialize wallet state";
                return PreserveUnreadable(path, ref reason);
            }

            if (state.Schema != 1)
            {
                reason = $"Unsupported wallet schema (expected 1, got {state.Schema})";
                return PreserveUnreadable(path, ref reason);
            }

            return state;
        }
        catch (Exception ex)
        {
            reason = $"Error loading wallet state: {ex.GetType().Name}";
            return PreserveUnreadable(path, ref reason);
        }
    }

    private static WalletState? PreserveUnreadable(string path, ref string? reason)
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
