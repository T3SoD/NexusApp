using System.Text;
using System.Text.Json;

namespace NexusApp.Services;

/// <summary>
/// Pure static parser for SCMDB (scmdb.net) blueprint-tracking export files (issue #3, historical
/// backfill import). Reads the { version, exportedAt, missions[], blueprints[] } shape documented
/// in the SCMDB import design spec, ground-truthed against two real user export samples kept
/// outside the repo. Headless and side-effect free - the caller owns reading the file from disk;
/// this only ever sees the already-loaded JSON text (RsiHandleParser precedent: pure static,
/// no exception ever escapes). Garbage input surfaces as a friendly Result.Error string instead
/// of throwing. tag/url/favorite are read-tolerated but unused - this issue does no tag matching.
/// </summary>
public static class ScmdbExportParser
{
    /// <summary>Reject input larger than this before attempting to parse it (SCMDB exports are
    /// normally a few hundred KB; anything past 5 MB is refused with a friendly message).</summary>
    public const int MaxInputBytes = 5 * 1024 * 1024;

    private const string InvalidFormatError = "This file doesn't look like a valid SCMDB export.";
    private const string TooLargeError = "This file is too large to import (over 5 MB).";

    /// <summary>
    /// Result of parsing one SCMDB export. <see cref="Error"/> is null on success; when set,
    /// every other field is the empty/zero default and must not be used.
    /// </summary>
    /// <param name="CompletedNames">Ordered blueprint display names with completed == true.</param>
    /// <param name="SkippedNotCompleted">Blueprint entries seen with completed == false.</param>
    /// <param name="MalformedEntries">Entries missing (or with an invalid) name or completed field; never crash, just skipped and counted.</param>
    /// <param name="MissionCount">Informational only; this issue never imports mission data.</param>
    /// <param name="Version">The export's declared "version" field (0 if absent/unreadable).</param>
    /// <param name="ExportedAt">The export's raw "exportedAt" string, display only.</param>
    /// <param name="NewerVersion">True when Version > 3: parsed on a best-effort basis (unknown
    /// fields are ignored by construction) so the UI can warn the export is newer than this build
    /// understands.</param>
    /// <param name="Error">Null on success; a single friendly message on any failure (garbage
    /// JSON, wrong root shape, missing blueprints array, oversized input).</param>
    public sealed record Result(
        IReadOnlyList<string> CompletedNames,
        int SkippedNotCompleted,
        int MalformedEntries,
        int MissionCount,
        int Version,
        string? ExportedAt,
        bool NewerVersion,
        string? Error)
    {
        public bool Success => Error is null;
    }

    private static Result Failure(string error) =>
        new(Array.Empty<string>(), 0, 0, 0, 0, null, false, error);

    /// <summary>Parses SCMDB export JSON text. Never throws.</summary>
    public static Result Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Failure(InvalidFormatError);
        if (Encoding.UTF8.GetByteCount(json) > MaxInputBytes) return Failure(TooLargeError);

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { return Failure(InvalidFormatError); }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Failure(InvalidFormatError);
            if (!root.TryGetProperty("blueprints", out var blueprintsEl) || blueprintsEl.ValueKind != JsonValueKind.Array)
                return Failure(InvalidFormatError);

            int version = root.TryGetProperty("version", out var vEl) && vEl.ValueKind == JsonValueKind.Number
                && vEl.TryGetInt32(out var v) ? v : 0;
            string? exportedAt = root.TryGetProperty("exportedAt", out var eaEl) && eaEl.ValueKind == JsonValueKind.String
                ? eaEl.GetString() : null;
            int missionCount = root.TryGetProperty("missions", out var missionsEl) && missionsEl.ValueKind == JsonValueKind.Array
                ? missionsEl.GetArrayLength() : 0;

            var names = new List<string>();
            int skipped = 0, malformed = 0;
            foreach (var bp in blueprintsEl.EnumerateArray())
            {
                if (bp.ValueKind != JsonValueKind.Object) { malformed++; continue; }

                bool hasName = bp.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String
                    && !string.IsNullOrEmpty(nameEl.GetString());
                bool hasCompleted = bp.TryGetProperty("completed", out var completedEl)
                    && (completedEl.ValueKind == JsonValueKind.True || completedEl.ValueKind == JsonValueKind.False);

                if (!hasName || !hasCompleted) { malformed++; continue; }
                if (completedEl.ValueKind == JsonValueKind.False) { skipped++; continue; }

                names.Add(nameEl.GetString()!);
            }

            return new Result(names, skipped, malformed, missionCount, version, exportedAt, version > 3, null);
        }
    }
}
