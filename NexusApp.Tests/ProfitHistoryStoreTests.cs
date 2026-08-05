using System.Text.Json;
using NexusApp.Models;
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

public class ProfitHistoryStoreTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    public void Dispose()
    {
        foreach (var d in _tempDirs)
        {
            try { if (Directory.Exists(d)) Directory.Delete(d, recursive: true); }
            catch { /* best effort */ }
        }
    }

    private string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nexus-profit-history-test-" + Path.GetRandomFileName());
        _tempDirs.Add(dir);
        return dir;
    }

    private static ProfitHistoryState SampleState() => new()
    {
        Schema = 1,
        Channels = new List<ChannelHistory>
        {
            new()
            {
                Channel = GameChannel.Live,
                FirstSessionUtc = new DateTime(2026, 6, 12, 9, 0, 0, DateTimeKind.Utc),
                PrunedNet = 42_000,
                PrunedCount = 3,
                Entries = new List<SessionSummary>
                {
                    new()
                    {
                        SessionKey = "Live|2026-07-04T12:00:00.0000000Z",
                        Channel = GameChannel.Live,
                        StartUtc = new DateTime(2026, 7, 4, 12, 0, 0, DateTimeKind.Utc),
                        LastSeenUtc = new DateTime(2026, 7, 4, 14, 30, 0, DateTimeKind.Utc),
                        Bought = 836_854, Sold = 1_067_200, Net = 230_346, TxCount = 3,
                    },
                }
            },
            new()
            {
                Channel = GameChannel.Ptu,
                FirstSessionUtc = new DateTime(2026, 7, 2, 20, 0, 0, DateTimeKind.Utc),
                Entries = new List<SessionSummary>
                {
                    new()
                    {
                        SessionKey = "Ptu|2026-07-02T20:00:00.0000000Z",
                        Channel = GameChannel.Ptu,
                        StartUtc = new DateTime(2026, 7, 2, 20, 0, 0, DateTimeKind.Utc),
                        LastSeenUtc = new DateTime(2026, 7, 2, 21, 0, 0, DateTimeKind.Utc),
                        Bought = 5000, Sold = 0, Net = -5000, TxCount = 1,
                    },
                }
            },
        }
    };

    [Fact]
    public void Save_LeavesNoTempFileAndRoundTrips()
    {
        var path = Path.Combine(TempDir(), "profit_history.json");
        var state = SampleState();

        Assert.True(ProfitHistoryStore.Save(path, state));
        Assert.False(File.Exists(path + ".tmp"), "Temp file should not be left behind");
        Assert.True(File.Exists(path), "History file should exist");

        var loaded = ProfitHistoryStore.Load(path, out var reason);
        Assert.NotNull(loaded);
        Assert.Null(reason);
        Assert.Equal(2, loaded!.Channels.Count);

        var live = loaded.Channels.Find(c => c.Channel == GameChannel.Live);
        Assert.NotNull(live);
        Assert.Equal(new DateTime(2026, 6, 12, 9, 0, 0, DateTimeKind.Utc), live!.FirstSessionUtc);
        Assert.Equal(DateTimeKind.Utc, live.FirstSessionUtc.Kind);
        Assert.Equal(42_000L, live.PrunedNet);
        Assert.Equal(3, live.PrunedCount);
        var entry = Assert.Single(live.Entries);
        Assert.Equal("Live|2026-07-04T12:00:00.0000000Z", entry.SessionKey);
        Assert.Equal(230_346L, entry.Net);
        Assert.Equal(3, entry.TxCount);
        Assert.Equal(DateTimeKind.Utc, entry.StartUtc.Kind);

        var ptu = loaded.Channels.Find(c => c.Channel == GameChannel.Ptu);
        Assert.Equal(-5000L, Assert.Single(ptu!.Entries).Net);
    }

    // Every failure reason may be logged verbatim into nexus.log, and the real history path lives
    // under %AppData% - so a reason that echoes the path (directly, or via a file-IO exception's
    // Message, which embeds it) would carry the Windows username into the log. The temp path used
    // here contains the username the same way, making this an exact stand-in for the leak.
    private static void AssertReasonCarriesNoPath(string path, string? reason)
    {
        Assert.NotNull(reason);
        Assert.NotEmpty(reason);
        Assert.DoesNotContain(Path.GetTempPath(), reason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Path.GetFileName(path), reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Load_MissingFile_ReturnsNullWithReason()
    {
        var path = Path.Combine(TempDir(), "missing.json");
        var loaded = ProfitHistoryStore.Load(path, out var reason);
        Assert.Null(loaded);
        AssertReasonCarriesNoPath(path, reason);
    }

    [Fact]
    public void Load_CorruptJson_ReturnsNullWithReason()
    {
        var path = Path.Combine(TempDir(), "corrupt.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ this is not valid json }");

        var loaded = ProfitHistoryStore.Load(path, out var reason);
        Assert.Null(loaded);
        AssertReasonCarriesNoPath(path, reason);
    }

    [Fact]
    public void Load_SchemaMismatch_ReturnsNullWithReason()
    {
        var path = Path.Combine(TempDir(), "schema2.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var state = SampleState();
        state.Schema = 2;
        File.WriteAllText(path, JsonSerializer.Serialize(state));

        var loaded = ProfitHistoryStore.Load(path, out var reason);
        Assert.Null(loaded);
        AssertReasonCarriesNoPath(path, reason);
    }

    // A load failure other than file-not-found must not let the next save destroy the only copy:
    // the unreadable file moves aside to <name>.bad.json with its bytes intact.
    [Fact]
    public void Load_SchemaMismatch_PreservesOriginalBytesAsBadFile()
    {
        var path = Path.Combine(TempDir(), "profit_history.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var state = SampleState();
        state.Schema = 2;
        var originalJson = JsonSerializer.Serialize(state);
        File.WriteAllText(path, originalJson);

        var loaded = ProfitHistoryStore.Load(path, out var reason);

        Assert.Null(loaded);
        var badPath = Path.Combine(Path.GetDirectoryName(path)!, "profit_history.bad.json");
        Assert.True(File.Exists(badPath), "Unreadable history should be preserved aside");
        Assert.Equal(originalJson, File.ReadAllText(badPath));
        Assert.False(File.Exists(path), "The unreadable file moves aside; a stale copy must not remain");
        AssertReasonCarriesNoPath(path, reason);
        Assert.Contains("preserved", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Load_CorruptJson_OverwritesOlderBadFile()
    {
        var path = Path.Combine(TempDir(), "profit_history.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var badPath = Path.Combine(Path.GetDirectoryName(path)!, "profit_history.bad.json");
        File.WriteAllText(badPath, "older bad");
        File.WriteAllText(path, "{ newer corrupt }");

        Assert.Null(ProfitHistoryStore.Load(path, out _));
        Assert.Equal("{ newer corrupt }", File.ReadAllText(badPath));
    }

    [Fact]
    public void Load_MissingFile_PreservesNothing()
    {
        var dir = TempDir();
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "profit_history.json");

        Assert.Null(ProfitHistoryStore.Load(path, out _));
        Assert.False(File.Exists(Path.Combine(dir, "profit_history.bad.json")));
    }

    [Fact]
    public void Load_OversizedFile_ReturnsNullWithoutParsing()
    {
        var path = Path.Combine(TempDir(), "oversized.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(new ProfitHistoryState());
        var padding = new string(' ', (int)(ProfitHistoryStore.MaxLoadBytes + 1 - json.Length));
        File.WriteAllText(path, json + padding);

        var loaded = ProfitHistoryStore.Load(path, out var reason);
        Assert.Null(loaded);
        AssertReasonCarriesNoPath(path, reason);
    }

    [Fact]
    public void Save_CreatesNestedDirectory()
    {
        var path = Path.Combine(TempDir(), "nested", "dir", "profit_history.json");
        Assert.True(ProfitHistoryStore.Save(path, new ProfitHistoryState()));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Save_ReturnsFalseOnFailure()
    {
        var tempDir = TempDir();
        Directory.CreateDirectory(tempDir);
        var blockedPath = Path.Combine(tempDir, "a");
        File.WriteAllText(blockedPath, "x");   // a file where the directory must go
        var path = Path.Combine(blockedPath, "profit_history.json");

        Assert.False(ProfitHistoryStore.Save(path, new ProfitHistoryState()));
    }

    [Fact]
    public void Load_EmptyState_RoundTrips()
    {
        var path = Path.Combine(TempDir(), "empty.json");
        Assert.True(ProfitHistoryStore.Save(path, new ProfitHistoryState()));
        var loaded = ProfitHistoryStore.Load(path, out _);
        Assert.NotNull(loaded);
        Assert.Empty(loaded!.Channels);
    }
}
