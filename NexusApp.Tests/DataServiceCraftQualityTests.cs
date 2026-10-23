using System.IO;
using Microsoft.Data.Sqlite;
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

public class DataServiceCraftQualityTests
{
    // Each test gets its own directory so the embedded seed is applied to a fresh db and no
    // test can see another's state (or the developer's real profile).
    private static void WithService(Action<DataService, string> body, Action<string>? preCreate = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "nexus-cq-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            preCreate?.Invoke(dir);
            using var svc = new DataService(dir);
            svc.Initialize();
            body(svc, dir);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void GetBlueprintFull_ReturnsSlotsAndCraftSeconds()
    {
        WithService((svc, _) =>
        {
            var bp = svc.GetBlueprintFull("P4-AR Rifle");
            Assert.NotNull(bp);
            Assert.Equal(3, bp!.Slots.Count);
            var barrel = Assert.Single(bp.Slots, s => s.Name == "Barrel");
            Assert.Equal(2, barrel.Modifiers.Count);
            Assert.Equal(150, bp.CraftSeconds);
        });
    }

    [Fact]
    public void GetBlueprintFull_ItemSlot_MapsToIsItem()
    {
        WithService((svc, _) =>
        {
            var bp = svc.GetBlueprintFull("Arbor MHV Mining Laser");
            Assert.NotNull(bp);
            var emitter = Assert.Single(bp!.Slots, s => s.Name == "Emitter");
            Assert.True(emitter.IsItem);
            Assert.Equal("units", emitter.Unit);
        });
    }

    [Fact]
    public void SeedAll_EmptySeed_LeavesTablesAndVersionAlone()
    {
        WithService((svc, dir) =>
        {
            long Count(string sql)
            {
                using var c = new SqliteConnection($"Data Source={Path.Combine(dir, "nexus.db")}");
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = sql;
                return Convert.ToInt64(cmd.ExecuteScalar());
            }
            var bpBefore = Count("SELECT COUNT(*) FROM blueprints");
            var ver = svc.MiningDataVersion;
            Assert.True(bpBefore > 0);

            Assert.False(svc.SeedAllEmptyForTest());

            Assert.Equal(bpBefore, Count("SELECT COUNT(*) FROM blueprints"));
            Assert.True(Count("SELECT COUNT(*) FROM quality_bands") > 0);
            Assert.Equal(ver, svc.MiningDataVersion);
        });
    }

    [Fact]
    public void GetQualityBands_Iron_FirstBandIs0To399Mapped325()
    {
        WithService((svc, _) =>
        {
            var bands = svc.GetQualityBands("Iron");
            Assert.NotEmpty(bands);
            Assert.Equal(new Models.QualityBand(0, 399, 325), bands[0]);
        });
    }

    [Fact]
    public void GetQualityBands_UnknownResource_IsEmpty()
    {
        WithService((svc, _) => Assert.Empty(svc.GetQualityBands("Unobtainium")));
    }

    [Fact]
    public void GetCraftStats_HasImpactForceWithLaserPowerOverride()
    {
        WithService((svc, _) =>
        {
            var stats = svc.GetCraftStats();
            var stat = stats["weapon_damage"];
            Assert.Equal("Impact Force", stat.Name);
            Assert.Equal("Laser Power", stat.Override!["WeaponMining"]);
        });
    }

    [Fact]
    public void IsStarterBlueprint_P4AR_True_MiningLaser_False()
    {
        WithService((svc, _) =>
        {
            Assert.True(svc.IsStarterBlueprint("P4-AR Rifle"));
            Assert.False(svc.IsStarterBlueprint("Arbor MHV Mining Laser"));
        });
    }

    [Fact]
    public void Migrate_AddsSlotsColumn_AndReseeds()
    {
        WithService((svc, dir) =>
        {
            Assert.Equal(3, svc.GetBlueprintFull("P4-AR Rifle")!.Slots.Count);

            using var conn = new SqliteConnection($"Data Source={Path.Combine(dir, "nexus.db")}");
            conn.Open();
            using var info = conn.CreateCommand();
            info.CommandText = "SELECT COUNT(*) FROM pragma_table_info('blueprints') WHERE name='slots_json'";
            Assert.Equal(1L, (long)info.ExecuteScalar()!);
            info.CommandText = "SELECT COUNT(*) FROM pragma_table_info('blueprints') WHERE name='craft_seconds'";
            Assert.Equal(1L, (long)info.ExecuteScalar()!);

            using var pin = conn.CreateCommand();
            pin.CommandText = "SELECT is_pinned FROM resources WHERE name='Agricium'";
            Assert.Equal(1L, (long)pin.ExecuteScalar()!);
        }, dir =>
        {
            // A pre-1.5.0 database: old blueprints columns only, one pinned resource. data_version equals the seed version, so only the column migration can trigger the reseed.
            using var conn = new SqliteConnection($"Data Source={Path.Combine(dir, "nexus.db")}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE blueprints (
                    id INTEGER PRIMARY KEY, name TEXT NOT NULL, category TEXT NOT NULL,
                    sub_category TEXT, unlock_faction TEXT, unlock_rep TEXT,
                    unlock_mission TEXT, unlock_system TEXT);
                CREATE TABLE resources (
                    id INTEGER PRIMARY KEY, name TEXT NOT NULL UNIQUE, base_rs INTEGER NOT NULL,
                    tier TEXT NOT NULL, rarity TEXT NOT NULL, method TEXT NOT NULL,
                    is_pinned INTEGER NOT NULL DEFAULT 0);
                CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                INSERT INTO resources (name,base_rs,tier,rarity,method,is_pinned) VALUES ('Agricium',1,'A','common','ship',1);
                INSERT INTO meta (key,value) VALUES ('data_version','1.5.0');";
            cmd.ExecuteNonQuery();
        });
    }
}
