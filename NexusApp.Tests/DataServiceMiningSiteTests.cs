using System.IO;
using Microsoft.Data.Sqlite;
using NexusApp.Models;
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

public class DataServiceMiningSiteTests
{
    // Each test gets its own directory so the embedded seed is applied to a fresh db and no
    // test can see another's state (or the developer's real profile).
    private static void WithService(Action<DataService, string> body, Action<string>? preCreate = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "nexus-ms-" + Guid.NewGuid().ToString("N"));
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
    public void Seed_FillsMiningSiteTables()
    {
        WithService((svc, _) =>
        {
            var sites = svc.GetAllMiningSites();
            Assert.Equal(47, sites.Count);
            var rocks = sites.SelectMany(s => s.Groups).SelectMany(g => g.Rocks).ToList();
            Assert.Equal(351, rocks.Count);
            Assert.Equal(216, rocks.Sum(r => r.Byproducts.Count));
        });
    }

    [Fact]
    public void GetSpawnsForResource_Beryl_HasAaronHaloPrimaryAndDaymarByproduct()
    {
        WithService((svc, _) =>
        {
            var rows = svc.GetSpawnsForResource("Beryl");
            Assert.Single(rows, r => r.Body == "Aaron Halo" && r.Kind == SpawnKind.Primary
                && r.Share == 18.0 && r.PackMin == 2 && r.PackMax == 4);
            Assert.Single(rows, r => r.Body == "Daymar" && r.Kind == SpawnKind.Byproduct
                && r.MinPct == 5.0 && r.MaxPct == 10.0 && r.Hosts.SequenceEqual(new[] { "Quantanium" }));
        });
    }

    [Fact]
    public void GetSpawnsForResource_Aphorite_HasDaymarHand()
    {
        WithService((svc, _) =>
        {
            var rows = svc.GetSpawnsForResource("Aphorite");
            Assert.Single(rows, r => r.Body == "Daymar" && r.Kind == SpawnKind.Hand && r.Share == 58.6);
        });
    }

    [Fact]
    public void GetSpawnsForResource_PrimaryWinsOverByproductOnSameBody()
    {
        WithService((svc, _) =>
        {
            var rows = svc.GetSpawnsForResource("Aluminium");
            var halo = Assert.Single(rows, r => r.Body == "Aaron Halo");
            Assert.Equal(SpawnKind.Primary, halo.Kind);
        });
    }

    [Fact]
    public void GetSpawnsForResource_ByproductHostsFollowRockWeightOrder()
    {
        WithService((svc, _) =>
        {
            var rows = svc.GetSpawnsForResource("Hephestanite");
            var daymar = Assert.Single(rows, r => r.Body == "Daymar");
            Assert.Equal(SpawnKind.Byproduct, daymar.Kind);
            Assert.Null(daymar.Share);
            Assert.Equal(new[] { "Silicon", "Quartz" }, daymar.Hosts);
        });
    }

    [Fact]
    public void GetMiningSite_Daymar_HasThreeRockGroupsAndPlants()
    {
        WithService((svc, _) =>
        {
            var site = svc.GetMiningSite("Daymar");
            Assert.NotNull(site);
            Assert.Equal("Stanton", site!.System);
            Assert.Equal(new[] { "ship", "roc", "fps", "plants" }, site.Groups.Select(g => g.Kind));
            var ship = site.Groups[0];
            Assert.Equal("Silicon", ship.Rocks[0].Ore);
            Assert.Equal("Quantanium", ship.Rocks[^1].Ore);
            Assert.Equal("Beryl", ship.Rocks[^1].Byproducts[0].Ore);
            Assert.Equal(1, site.Groups.Single(g => g.Kind == "plants").Count);
        });
    }

    [Fact]
    public void GetMiningSite_AaronHalo_HasOnlyShipGroup()
    {
        WithService((svc, _) =>
        {
            var site = svc.GetMiningSite("Aaron Halo");
            Assert.NotNull(site);
            Assert.Equal(new[] { "ship" }, site!.Groups.Select(g => g.Kind));
        });
    }

    [Fact]
    public void GetMiningSite_Unknown_ReturnsNull_AndSpawns_Unknown_Empty()
    {
        WithService((svc, _) =>
        {
            Assert.Null(svc.GetMiningSite("Nowhere"));
            Assert.Empty(svc.GetSpawnsForResource("Unobtainium"));
        });
    }

    [Fact]
    public void Reseed_From_1_5_0_FillsTables()
    {
        WithService((svc, dir) =>
        {
            var sites = svc.GetAllMiningSites();
            Assert.Equal(47, sites.Count);
            var rocks = sites.SelectMany(s => s.Groups).SelectMany(g => g.Rocks).ToList();
            Assert.Equal(351, rocks.Count);
            Assert.Equal(216, rocks.Sum(r => r.Byproducts.Count));
            Assert.Equal("1.5.1", svc.MiningDataVersion);
        },
        preCreate: dir =>
        {
            using (var first = new DataService(dir)) first.Initialize();
            SqliteConnection.ClearAllPools();
            using var conn = new SqliteConnection($"Data Source={Path.Combine(dir, "nexus.db")}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE meta SET value='1.5.0' WHERE key='data_version'; " +
                "DELETE FROM mining_site_byproducts; DELETE FROM mining_site_rocks; DELETE FROM mining_site_groups;";
            cmd.ExecuteNonQuery();
            conn.Close();
            SqliteConnection.ClearAllPools();
        });
    }
}
