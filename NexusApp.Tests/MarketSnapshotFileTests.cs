using System.Text.Json;
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

public class MarketSnapshotFileTests : IDisposable
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
        var dir = Path.Combine(Path.GetTempPath(), "nexus-market-snapshot-test-" + Path.GetRandomFileName());
        _tempDirs.Add(dir);
        return dir;
    }

    [Fact]
    public void Save_LeavesNoTempFileAndRoundTrips()
    {
        var path = Path.Combine(TempDir(), "market.json");
        var snap = new MarketSnapshot
        {
            Schema = 1,
            LiveGameVersion = "4.9.1",
            Commodities = new MarketDataset<MarketCommodity>
            {
                FetchedUtc = new DateTime(2026, 7, 27, 12, 0, 0, DateTimeKind.Utc),
                Rows = new List<MarketCommodity>
                {
                    new MarketCommodity(1, "Aluminum", "aluminum", true, false, 0),
                    new MarketCommodity(2, "Gold", "gold", false, true, 0),
                }
            },
            RawPrices = new MarketDataset<MarketPriceRow>
            {
                FetchedUtc = new DateTime(2026, 7, 27, 13, 0, 0, DateTimeKind.Utc),
                Rows = new List<MarketPriceRow>
                {
                    new MarketPriceRow(100, 1, 50.5, 50.0, "4.9.1", new DateTime(2026, 7, 27, 11, 0, 0, DateTimeKind.Utc), "Terminal A"),
                }
            },
            RefinedPrices = new MarketDataset<MarketPriceRow>
            {
                FetchedUtc = new DateTime(2026, 7, 27, 14, 0, 0, DateTimeKind.Utc),
                Rows = new List<MarketPriceRow>
                {
                    new MarketPriceRow(101, 2, 60.0, 59.5, "4.9.1", new DateTime(2026, 7, 27, 12, 0, 0, DateTimeKind.Utc), "Terminal B"),
                }
            },
            Yields = new MarketDataset<MarketYieldRow>
            {
                FetchedUtc = new DateTime(2026, 7, 27, 15, 0, 0, DateTimeKind.Utc),
                Rows = new List<MarketYieldRow>
                {
                    new MarketYieldRow(102, 3, 10, 12, new DateTime(2026, 7, 27, 13, 0, 0, DateTimeKind.Utc), "Refinery C"),
                }
            },
            Terminals = new MarketDataset<MarketTerminal>
            {
                FetchedUtc = new DateTime(2026, 7, 27, 16, 0, 0, DateTimeKind.Utc),
                Rows = new List<MarketTerminal>
                {
                    new MarketTerminal(100, "Terminal A", "Trade", false, "Stanton", "Crusader"),
                }
            }
        };

        Assert.True(MarketSnapshotFile.Save(path, snap));
        Assert.False(File.Exists(path + ".tmp"), "Temp file should not be left behind");
        Assert.True(File.Exists(path), "Snapshot file should exist");

        var loaded = MarketSnapshotFile.Load(path, out var reason);
        Assert.NotNull(loaded);
        Assert.Null(reason);
        Assert.Equal(snap.Schema, loaded.Schema);
        Assert.Equal(snap.LiveGameVersion, loaded.LiveGameVersion);

        // Commodities
        Assert.Equal(snap.Commodities.FetchedUtc, loaded.Commodities.FetchedUtc);
        Assert.Equal(DateTimeKind.Utc, loaded.Commodities.FetchedUtc.Kind);
        Assert.Equal(snap.Commodities.Rows.Count, loaded.Commodities.Rows.Count);
        Assert.Equal("Aluminum", loaded.Commodities.Rows[0].Name);

        // RawPrices
        Assert.Equal(snap.RawPrices.FetchedUtc, loaded.RawPrices.FetchedUtc);
        var rawPrice = Assert.Single(loaded.RawPrices.Rows);
        Assert.Equal(50.5, rawPrice.Sell);
        Assert.Equal("Terminal A", rawPrice.TerminalName);

        // RefinedPrices
        Assert.Equal(snap.RefinedPrices.FetchedUtc, loaded.RefinedPrices.FetchedUtc);
        var refinedPrice = Assert.Single(loaded.RefinedPrices.Rows);
        Assert.Equal(59.5, refinedPrice.SellAvgWeek);

        // Yields
        Assert.Equal(snap.Yields.FetchedUtc, loaded.Yields.FetchedUtc);
        var yieldRow = Assert.Single(loaded.Yields.Rows);
        Assert.Equal(10, yieldRow.BonusPct);
        Assert.Equal(DateTimeKind.Utc, yieldRow.ModifiedUtc.Kind);

        // Terminals
        Assert.Equal(snap.Terminals.FetchedUtc, loaded.Terminals.FetchedUtc);
        var terminal = Assert.Single(loaded.Terminals.Rows);
        Assert.Equal("Stanton", terminal.System);
        Assert.Equal("Crusader", terminal.Location);
    }

    [Fact]
    public void Load_MissingFile_ReturnsNullWithReason()
    {
        var path = Path.Combine(TempDir(), "missing.json");
        var loaded = MarketSnapshotFile.Load(path, out var reason);
        Assert.Null(loaded);
        Assert.NotNull(reason);
        Assert.NotEmpty(reason);
    }

    [Fact]
    public void Load_OversizedFile_ReturnsNullWithoutParsing()
    {
        var path = Path.Combine(TempDir(), "oversized.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // Create a valid snapshot and serialize it
        var snap = new MarketSnapshot { Schema = 1, LiveGameVersion = "4.9.1" };
        var opts = new JsonSerializerOptions { WriteIndented = false };
        var json = JsonSerializer.Serialize(snap, opts);

        // Pad the JSON with trailing spaces to exceed MaxLoadBytes.
        // This ensures the file is valid JSON but rejected by the length check,
        // proving the check-length-first behavior.
        var totalSize = MarketSnapshotFile.MaxLoadBytes + 1;
        var padding = new string(' ', (int)(totalSize - json.Length));
        File.WriteAllText(path, json + padding);

        var loaded = MarketSnapshotFile.Load(path, out var reason);
        Assert.Null(loaded);
        Assert.NotNull(reason);
        Assert.NotEmpty(reason);
    }

    [Fact]
    public void Load_CorruptJson_ReturnsNullWithReason()
    {
        var path = Path.Combine(TempDir(), "corrupt.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ this is not valid json }");

        var loaded = MarketSnapshotFile.Load(path, out var reason);
        Assert.Null(loaded);
        Assert.NotNull(reason);
        Assert.NotEmpty(reason);
    }

    [Fact]
    public void Load_SchemaMismatch_ReturnsNullWithReason()
    {
        var path = Path.Combine(TempDir(), "schema2.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var snap = new MarketSnapshot
        {
            Schema = 2,  // Wrong schema
            LiveGameVersion = "4.9.1",
        };
        var opts = new JsonSerializerOptions { WriteIndented = false };
        File.WriteAllText(path, JsonSerializer.Serialize(snap, opts));

        var loaded = MarketSnapshotFile.Load(path, out var reason);
        Assert.Null(loaded);
        Assert.NotNull(reason);
        Assert.NotEmpty(reason);
    }

    [Fact]
    public void Save_CreatesNestedDirectory()
    {
        var path = Path.Combine(TempDir(), "nested", "dir", "market.json");
        var snap = new MarketSnapshot { Schema = 1, LiveGameVersion = "4.9.1" };

        Assert.True(MarketSnapshotFile.Save(path, snap));
        Assert.True(Directory.Exists(Path.GetDirectoryName(path)));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Save_ReturnsFalseOnFailure()
    {
        // Try to save to a blocked path: create a file where a directory is needed.
        var tempDir = TempDir();
        Directory.CreateDirectory(tempDir);
        var blockedPath = Path.Combine(tempDir, "a");
        // Create a file at "a" so "a/market.json" will fail when trying to create directory
        File.WriteAllText(blockedPath, "x");
        var path = Path.Combine(blockedPath, "market.json");
        var snap = new MarketSnapshot { Schema = 1, LiveGameVersion = "4.9.1" };

        Assert.False(MarketSnapshotFile.Save(path, snap));
    }

    [Fact]
    public void Load_EmptyDatasets_RoundTrips()
    {
        var path = Path.Combine(TempDir(), "empty.json");
        var snap = new MarketSnapshot
        {
            Schema = 1,
            LiveGameVersion = "4.9",
            Commodities = new(),
            RawPrices = new(),
            RefinedPrices = new(),
            Yields = new(),
            Terminals = new()
        };

        Assert.True(MarketSnapshotFile.Save(path, snap));
        var loaded = MarketSnapshotFile.Load(path, out _);
        Assert.NotNull(loaded);
        Assert.Empty(loaded.Commodities.Rows);
        Assert.Empty(loaded.RawPrices.Rows);
    }
}
