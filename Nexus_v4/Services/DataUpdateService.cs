using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace Nexus_v4.Services;

/// <summary>
/// Fail-quiet, offline-safe background check for newer mining data. Does a conditional
/// (ETag) GET of the committed seed file on GitHub; if it changed and its dataVersion is
/// newer than what's applied, it stages the file into the local cache for the next launch
/// to pick up (see DataService). Never throws and never blocks the app.
/// </summary>
public static class DataUpdateService
{
    // The seed committed to the repo is the single source of truth — no separate manifest.
    private const string SeedUrl =
        "https://raw.githubusercontent.com/T3SoD/NexusApp/main/Nexus_v4/Data/seed_data.json";

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };

    /// <summary>Returns the staged version if a newer seed was downloaded, otherwise null.</summary>
    public static async Task<string?> CheckAsync(string appliedVersion)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, SeedUrl);
            var etag = App.Settings.Current.DataEtag;
            if (!string.IsNullOrEmpty(etag))
                req.Headers.TryAddWithoutValidation("If-None-Match", etag);

            using var resp = await _http.SendAsync(req);
            if (resp.StatusCode == HttpStatusCode.NotModified) return null;
            if (!resp.IsSuccessStatusCode) return null;

            var json = await resp.Content.ReadAsStringAsync();
            var version = ExtractValidVersion(json);
            if (version == null) return null;   // malformed / not a real seed — ignore

            // Remember the ETag so unchanged files return 304 next time.
            App.Settings.Current.DataEtag = resp.Headers.ETag?.Tag ?? App.Settings.Current.DataEtag;
            App.Settings.Current.LastDataUpdate = DateTime.UtcNow;
            App.Settings.Save();

            if (DataService.CompareVersions(version, appliedVersion) <= 0)
                return null;   // not newer than what's applied — nothing to stage

            // Stage atomically: write to a temp file, then move into place.
            var tmp = DataService.CachedSeedPath + ".tmp";
            await File.WriteAllTextAsync(tmp, json);
            File.Move(tmp, DataService.CachedSeedPath, overwrite: true);
            return version;
        }
        catch
        {
            return null;   // offline, timeout, IO — silently do nothing
        }
    }

    /// <summary>Parses just enough to confirm it's a real seed (has resources) and returns its dataVersion.</summary>
    private static string? ExtractValidVersion(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            // New key is miningDataVersion; fall back to the legacy dataVersion.
            if ((!root.TryGetProperty("miningDataVersion", out var v) || v.ValueKind != JsonValueKind.String) &&
                (!root.TryGetProperty("dataVersion", out v) || v.ValueKind != JsonValueKind.String)) return null;
            if (!root.TryGetProperty("resources", out var r) || r.ValueKind != JsonValueKind.Array || r.GetArrayLength() == 0) return null;
            return v.GetString();
        }
        catch { return null; }
    }
}
