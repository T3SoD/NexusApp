using System.IO;
using System.Text.Json;
using NexusApp.Models;

namespace NexusApp.Services;

public sealed class AutoLoadSampleStore
{
    private readonly string _path;
    private readonly object _gate = new();

    public AutoLoadSampleStore(string? path = null)
        => _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "NexusApp", "autoload_samples.json");

    public void Append(AutoLoadSample sample)
    {
        lock (_gate)
        {
            try
            {
                var all = ReadAllUnlocked();
                all.Add(sample);
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.WriteAllText(_path, JsonSerializer.Serialize(all,
                    new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                Logger.Info($"[CARGO] auto-load sample write failed: {ex.Message}");
            }
        }
    }

    public IReadOnlyList<AutoLoadSample> ReadAll() { lock (_gate) return ReadAllUnlocked(); }

    private List<AutoLoadSample> ReadAllUnlocked()
    {
        try
        {
            if (!File.Exists(_path)) return new();
            return JsonSerializer.Deserialize<List<AutoLoadSample>>(File.ReadAllText(_path)) ?? new();
        }
        catch (Exception ex) { Logger.Info($"[CARGO] auto-load sample read failed: {ex.Message}"); return new(); }
    }
}
