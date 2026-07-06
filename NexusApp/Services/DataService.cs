using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NexusApp.Models;

namespace NexusApp.Services;

public class DataService : IDisposable
{
    private static readonly string _dataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NexusApp");

    private static readonly string _dbPath = Path.Combine(_dataDir, "nexus.db");

    private static readonly JsonSerializerOptions _jsonOpts = new() { PropertyNameCaseInsensitive = true };

    private SqliteConnection? _conn;
    private string _seedVersion = "0.0.0";

    /// <summary>The mining-data version currently applied to the database. This is the
    /// seed-content version (auto-updatable) - distinct from <see cref="GameData.Version"/>,
    /// which tracks the Star Citizen patch and is bumped manually.</summary>
    public string MiningDataVersion => GetMeta("data_version") ?? _seedVersion;

    public void Initialize()
    {
        Directory.CreateDirectory(_dataDir);
        _conn = new SqliteConnection($"Data Source={_dbPath}");
        _conn.Open();
        CreateSchema();
        MigrateColumns();
        ApplySeed();
    }

    private void CreateSchema()
    {
        Exec(@"
            CREATE TABLE IF NOT EXISTS resources (
                id INTEGER PRIMARY KEY,
                name TEXT NOT NULL UNIQUE,
                base_rs INTEGER NOT NULL,
                tier TEXT NOT NULL,
                rarity TEXT NOT NULL,
                method TEXT NOT NULL,
                is_pinned INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS resource_locations (
                resource_name TEXT NOT NULL,
                location TEXT NOT NULL,
                system TEXT NOT NULL,
                PRIMARY KEY (resource_name, location)
            );
            CREATE TABLE IF NOT EXISTS resource_refineries (
                resource_name TEXT NOT NULL,
                station TEXT NOT NULL,
                system TEXT NOT NULL,
                modifier_pct INTEGER NOT NULL,
                PRIMARY KEY (resource_name, station)
            );
            CREATE TABLE IF NOT EXISTS resource_found_in (
                resource_name TEXT NOT NULL,
                host_ore TEXT NOT NULL,
                min_pct REAL NOT NULL,
                max_pct REAL NOT NULL,
                probability REAL NOT NULL,
                variants INTEGER NOT NULL,
                PRIMARY KEY (resource_name, host_ore)
            );
            CREATE TABLE IF NOT EXISTS blueprints (
                id INTEGER PRIMARY KEY,
                name TEXT NOT NULL,
                category TEXT NOT NULL,
                sub_category TEXT,
                unlock_faction TEXT,
                unlock_rep TEXT,
                unlock_mission TEXT,
                unlock_system TEXT
            );
            CREATE TABLE IF NOT EXISTS blueprint_ingredients (
                blueprint_id INTEGER NOT NULL,
                resource_name TEXT NOT NULL,
                quantity REAL NOT NULL,
                unit TEXT NOT NULL DEFAULT 'SCU',
                FOREIGN KEY (blueprint_id) REFERENCES blueprints(id)
            );
            CREATE TABLE IF NOT EXISTS work_orders (
                id TEXT PRIMARY KEY,
                label TEXT NOT NULL,
                resources TEXT NOT NULL,
                location TEXT NOT NULL,
                refinery TEXT NOT NULL,
                status INTEGER NOT NULL DEFAULT 0,
                notes TEXT NOT NULL DEFAULT '',
                created_at TEXT NOT NULL,
                timer_start TEXT,
                timer_end TEXT
            );
            CREATE TABLE IF NOT EXISTS shopping_list (
                resource_name TEXT PRIMARY KEY,
                quantity REAL NOT NULL,
                unit TEXT NOT NULL DEFAULT 'SCU'
            );
            CREATE TABLE IF NOT EXISTS blueprint_unlocks (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                blueprint_name TEXT NOT NULL,
                faction TEXT NOT NULL,
                mission_type TEXT,
                mission_title TEXT NOT NULL,
                rank TEXT,
                systems TEXT
            );
            CREATE TABLE IF NOT EXISTS meta (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
        ");
    }

    private bool _forceReseed;

    private void MigrateColumns()
    {
        try { Exec("ALTER TABLE work_orders ADD COLUMN timer_start TEXT"); } catch { }
        try { Exec("ALTER TABLE work_orders ADD COLUMN timer_end TEXT"); } catch { }
        // If sub_category is missing the blueprint rows predate it - force a full reseed.
        try { Exec("ALTER TABLE blueprints ADD COLUMN sub_category TEXT"); _forceReseed = true; } catch { }
    }

    // ── Versioned seeding ───────────────────────────────────────────────────────

    /// <summary>Loads the mining-data seed embedded in this build. Reference data ships
    /// with the app and only changes when a new version is installed.</summary>
    private static SeedData LoadSeed(byte[]? bytes)
    {
        return TryParseSeed(bytes) ?? new SeedData("0.0.0", null,
            new List<SeedResource>(), new Dictionary<string, string>(),
            new List<SeedBlueprint>(), null);
    }

    private static byte[]? ReadEmbeddedSeedBytes()
    {
        using var stream = typeof(DataService).Assembly
            .GetManifestResourceStream("NexusApp.Data.seed_data.json");
        if (stream == null) return null;
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    // Streams just the top-level version property out of the seed JSON without
    // building the full object graph - keeps cold start cheap on every launch.
    private static string? ReadSeedVersion(byte[]? bytes)
    {
        if (bytes == null || bytes.Length == 0) return null;
        try
        {
            var reader = new Utf8JsonReader(bytes);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return null;
            string? mining = null, legacy = null;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var prop = reader.GetString();
                reader.Read(); // advance to the value
                if (prop == "miningDataVersion" && reader.TokenType == JsonTokenType.String) mining = reader.GetString();
                else if (prop == "dataVersion" && reader.TokenType == JsonTokenType.String) legacy = reader.GetString();
                else reader.Skip(); // skip arrays/objects we don't need
            }
            return mining ?? legacy;
        }
        catch (Exception ex) { Logger.Error("Failed to read seed version", ex); return null; }
    }

    private static SeedData? TryParseSeed(byte[]? bytes)
    {
        if (bytes == null || bytes.Length == 0) return null;
        try { return JsonSerializer.Deserialize<SeedData>(bytes, _jsonOpts); }
        catch (Exception ex) { Logger.Error("Failed to parse embedded seed", ex); return null; }
    }

    private void ApplySeed()
    {
        // Read the raw seed bytes once and probe only the version string - the common
        // path (no reseed) never pays to deserialize the whole ~1 MB payload.
        var bytes   = ReadEmbeddedSeedBytes();
        var seedVer = ReadSeedVersion(bytes);
        _seedVersion = string.IsNullOrWhiteSpace(seedVer) ? "0.0.0" : seedVer!;

        var resourceCount = Scalar<long>("SELECT COUNT(*) FROM resources");
        var applied = GetMeta("data_version");

        // Backfill byproduct sourcing for DBs seeded before the resource_found_in table
        // existed (the table is created empty by CreateSchema). Idempotent: only fills when
        // empty, and a later same-run reseed re-populates it anyway. Fresh installs skip this
        // (resourceCount == 0) and get it via SeedAll below.
        if (resourceCount > 0 && Scalar<long>("SELECT COUNT(*) FROM resource_found_in") == 0)
            ReseedFoundIn(LoadSeed(bytes));

        // Fresh install - seed everything.
        if (resourceCount == 0)
        {
            SeedAll(LoadSeed(bytes));
            SetMeta("data_version", _seedVersion);
            return;
        }

        // Old schema that just gained a column - repopulate from the current seed.
        if (_forceReseed)
        {
            SeedAll(LoadSeed(bytes));
            SetMeta("data_version", _seedVersion);
            return;
        }

        // Pre-versioning DB already populated by an earlier build - adopt the current
        // version without a disruptive reseed; backfill unlocks if missing (v4.1).
        if (applied == null)
        {
            if (Scalar<long>("SELECT COUNT(*) FROM blueprint_unlocks") == 0) SeedUnlocks(LoadSeed(bytes));
            SetMeta("data_version", _seedVersion);
            return;
        }

        // A newer build shipped newer embedded data - full reseed.
        if (CompareVersions(_seedVersion, applied) > 0)
        {
            SeedAll(LoadSeed(bytes));
            SetMeta("data_version", _seedVersion);
        }
        // Otherwise: nothing to do, and the full seed was never deserialized.
    }

    /// <summary>Replaces all reference data from <paramref name="seed"/>, preserving the
    /// user's pinned resources (the only user state stored on a reseeded table).</summary>
    private void SeedAll(SeedData seed)
    {
        var pinned = new List<string>();
        using (var cmd = _conn!.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM resources WHERE is_pinned=1";
            using var rdr = cmd.ExecuteReader();
            while (rdr.Read()) pinned.Add(rdr.GetString(0));
        }

        using var tx = _conn!.BeginTransaction();

        Exec("DELETE FROM resource_locations");
        Exec("DELETE FROM resource_refineries");
        Exec("DELETE FROM resource_found_in");
        Exec("DELETE FROM resources");
        Exec("DELETE FROM blueprint_ingredients");
        Exec("DELETE FROM blueprints");
        Exec("DELETE FROM blueprint_unlocks");

        foreach (var r in seed.Resources)
        {
            Exec("INSERT OR IGNORE INTO resources (name,base_rs,tier,rarity,method) VALUES (@n,@r,@t,@ra,@m)",
                ("@n", r.Name), ("@r", r.BaseRs), ("@t", r.Tier), ("@ra", r.Rarity), ("@m", r.Method));

            foreach (var loc in r.Locations)
            {
                var sys = seed.LocationSystems.TryGetValue(loc, out var s) ? s : "Unknown";
                Exec("INSERT OR IGNORE INTO resource_locations VALUES (@r,@l,@s)",
                    ("@r", r.Name), ("@l", loc), ("@s", sys));
            }

            foreach (var ref_ in r.Refineries)
                Exec("INSERT OR IGNORE INTO resource_refineries VALUES (@r,@st,@sy,@m)",
                    ("@r", r.Name), ("@st", ref_.Station), ("@sy", ref_.System), ("@m", ref_.ModifierPct));

            foreach (var f in r.FoundIn ?? [])
                Exec("INSERT OR IGNORE INTO resource_found_in VALUES (@r,@h,@mn,@mx,@p,@v)",
                    ("@r", r.Name), ("@h", f.Ore), ("@mn", f.MinPct), ("@mx", f.MaxPct),
                    ("@p", f.Probability), ("@v", f.Variants));
        }

        foreach (var bp in seed.Blueprints)
        {
            Exec("INSERT INTO blueprints (name,category,sub_category) VALUES (@n,@c,@sc)",
                ("@n", bp.Name), ("@c", bp.Category),
                ("@sc", (object?)bp.SubCategory ?? DBNull.Value));

            var bpId = Scalar<long>("SELECT last_insert_rowid()");
            foreach (var ing in bp.Ingredients)
                Exec("INSERT INTO blueprint_ingredients VALUES (@bid,@r,@q,@u)",
                    ("@bid", bpId), ("@r", ing.ResourceName), ("@q", ing.Quantity), ("@u", ing.Unit));
        }

        InsertUnlocks(seed);

        foreach (var name in pinned)
            Exec("UPDATE resources SET is_pinned=1 WHERE name=@n", ("@n", name));

        tx.Commit();
    }

    private void SeedUnlocks(SeedData seed)
    {
        using var tx = _conn!.BeginTransaction();
        Exec("DELETE FROM blueprint_unlocks");
        InsertUnlocks(seed);
        tx.Commit();
    }

    // Standalone (re)load of just the byproduct-sourcing table, for backfilling DBs seeded
    // before the table existed without a disruptive full reseed.
    private void ReseedFoundIn(SeedData seed)
    {
        using var tx = _conn!.BeginTransaction();
        Exec("DELETE FROM resource_found_in");
        foreach (var r in seed.Resources)
            foreach (var f in r.FoundIn ?? [])
                Exec("INSERT OR IGNORE INTO resource_found_in VALUES (@r,@h,@mn,@mx,@p,@v)",
                    ("@r", r.Name), ("@h", f.Ore), ("@mn", f.MinPct), ("@mx", f.MaxPct),
                    ("@p", f.Probability), ("@v", f.Variants));
        tx.Commit();
    }

    private void InsertUnlocks(SeedData seed)
    {
        if (seed.BlueprintUnlocks == null) return;
        foreach (var u in seed.BlueprintUnlocks)
        {
            var systems = u.Systems is { Count: > 0 } ? string.Join(",", u.Systems) : null;
            Exec("INSERT INTO blueprint_unlocks (blueprint_name,faction,mission_type,mission_title,rank,systems) VALUES (@bn,@f,@mt,@t,@r,@s)",
                ("@bn", u.BlueprintName), ("@f", u.Faction),
                ("@mt", (object?)u.MissionType ?? DBNull.Value),
                ("@t", u.MissionTitle),
                ("@r", (object?)u.Rank ?? DBNull.Value),
                ("@s", (object?)systems ?? DBNull.Value));
        }
    }

    // ── Meta / version helpers ──────────────────────────────────────────────────

    private string? GetMeta(string key)
    {
        if (_conn == null) return null;
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE key=@k";
        cmd.Parameters.AddWithValue("@k", key);
        return cmd.ExecuteScalar() as string;
    }

    private void SetMeta(string key, string value) =>
        Exec("INSERT INTO meta(key,value) VALUES(@k,@v) ON CONFLICT(key) DO UPDATE SET value=@v",
            ("@k", key), ("@v", value));

    /// <summary>Dotted numeric compare ("1.2.0" vs "1.10.0"); non-numeric parts count as 0.</summary>
    public static int CompareVersions(string a, string b)
    {
        static int[] Parts(string v)
        {
            var s = v.Split('.');
            var r = new int[s.Length];
            for (int i = 0; i < s.Length; i++) r[i] = int.TryParse(s[i], out var n) ? n : 0;
            return r;
        }
        var pa = Parts(a);
        var pb = Parts(b);
        for (int i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            int x = i < pa.Length ? pa[i] : 0;
            int y = i < pb.Length ? pb[i] : 0;
            if (x != y) return x.CompareTo(y);
        }
        return 0;
    }

    // ── Reads ─────────────────────────────────────────────────────────────────

    public List<Resource> GetAllResources()
    {
        var resources = new Dictionary<string, Resource>();

        using var cmd = _conn!.CreateCommand();
        cmd.CommandText = "SELECT name,base_rs,tier,rarity,method,is_pinned FROM resources ORDER BY base_rs";
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
        {
            resources[rdr.GetString(0)] = new Resource
            {
                Name = rdr.GetString(0), BaseRs = rdr.GetInt32(1),
                Tier = rdr.GetString(2), Rarity = rdr.GetString(3),
                Method = rdr.GetString(4), IsPinned = rdr.GetInt32(5) == 1
            };
        }

        using var locCmd = _conn.CreateCommand();
        locCmd.CommandText = "SELECT resource_name, location FROM resource_locations ORDER BY location";
        using var locRdr = locCmd.ExecuteReader();
        while (locRdr.Read())
        {
            var name = locRdr.GetString(0);
            if (resources.TryGetValue(name, out var r))
                r.Locations.Add(locRdr.GetString(1));
        }

        using var refCmd = _conn.CreateCommand();
        refCmd.CommandText = "SELECT resource_name,station,system,modifier_pct FROM resource_refineries ORDER BY modifier_pct DESC";
        using var refRdr = refCmd.ExecuteReader();
        while (refRdr.Read())
        {
            var name = refRdr.GetString(0);
            if (resources.TryGetValue(name, out var r))
                r.Refineries.Add(new RefineryYield(refRdr.GetString(1), refRdr.GetString(2), refRdr.GetInt32(3)));
        }

        return [.. resources.Values];
    }

    public List<RsMatch> FindByRs(int rs)
    {
        var all = GetAllResources();
        var results = new List<RsMatch>();
        foreach (var r in all.Where(r => r.Method == "ship"))
        {
            var (matches, nodes, isExact, errorPct) = r.CheckRs(rs);
            if (matches)
                results.Add(new RsMatch(r, nodes, isExact, errorPct));
        }
        return results
            .OrderByDescending(x => x.Resource.IsPinned)
            .ThenBy(x => x.ErrorPct)
            .ToList();
    }

    public List<Blueprint> GetBlueprintsForResource(string resourceName)
    {
        var result = new List<Blueprint>();

        using var cmd = _conn!.CreateCommand();
        cmd.CommandText = @"
            SELECT b.id, b.name, b.category, b.sub_category
            FROM blueprints b
            JOIN blueprint_ingredients i ON b.id = i.blueprint_id
            WHERE i.resource_name = @r
            ORDER BY b.category, b.sub_category, b.name";
        cmd.Parameters.AddWithValue("@r", resourceName);

        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
        {
            var bp = new Blueprint
            {
                Name = rdr.GetString(1), Category = rdr.GetString(2),
                SubCategory = rdr.IsDBNull(3) ? null : rdr.GetString(3),
            };
            var bpId = rdr.GetInt64(0);

            using var ingCmd = _conn.CreateCommand();
            ingCmd.CommandText = "SELECT resource_name,quantity,unit FROM blueprint_ingredients WHERE blueprint_id=@id";
            ingCmd.Parameters.AddWithValue("@id", bpId);
            using var ingRdr = ingCmd.ExecuteReader();
            while (ingRdr.Read())
                bp.Ingredients.Add(new BlueprintIngredient(ingRdr.GetString(0), ingRdr.GetDouble(1), ingRdr.GetString(2)));

            result.Add(bp);
        }

        return result;
    }

    /// <summary>Other ores' deposits that also yield <paramref name="resourceName"/> as a
    /// byproduct, best spawn chance first. Reference data (datamined); empty for ores that are
    /// only ever a headline ore or have no datamined composition.</summary>
    public List<FoundInSource> GetFoundInForResource(string resourceName)
    {
        var result = new List<FoundInSource>();
        using var cmd = _conn!.CreateCommand();
        cmd.CommandText = @"
            SELECT host_ore, min_pct, max_pct, probability, variants
            FROM resource_found_in
            WHERE resource_name = @r
            ORDER BY probability DESC, max_pct DESC, host_ore";
        cmd.Parameters.AddWithValue("@r", resourceName);

        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
            result.Add(new FoundInSource(
                rdr.GetString(0), rdr.GetDouble(1), rdr.GetDouble(2), rdr.GetDouble(3), rdr.GetInt32(4)));
        return result;
    }

    public List<Blueprint> SearchBlueprints(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        var result = new List<Blueprint>();
        using var cmd = _conn!.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT id,name,category FROM blueprints WHERE LOWER(name) LIKE @q ORDER BY name LIMIT 50";
        cmd.Parameters.AddWithValue("@q", $"%{query.ToLower()}%");
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
            result.Add(new Blueprint { Name = rdr.GetString(1), Category = rdr.GetString(2) });
        return result;
    }

    public List<Blueprint> GetAllBlueprints()
    {
        var result = new List<Blueprint>();
        using var cmd = _conn!.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT name,category,sub_category FROM blueprints ORDER BY category, name";
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
            result.Add(new Blueprint
            {
                Name = rdr.GetString(0), Category = rdr.GetString(1),
                SubCategory = rdr.IsDBNull(2) ? null : rdr.GetString(2),
            });
        return result;
    }

    public HashSet<string> GetResourceNamesForBlueprintSearch(string query)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(query)) return result;
        using var cmd = _conn!.CreateCommand();
        cmd.CommandText = @"
            SELECT DISTINCT bi.resource_name
            FROM blueprint_ingredients bi
            JOIN blueprints b ON b.id = bi.blueprint_id
            WHERE LOWER(b.name) LIKE @q";
        cmd.Parameters.AddWithValue("@q", $"%{query.ToLower()}%");
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read()) result.Add(rdr.GetString(0));
        return result;
    }

    public Blueprint? GetBlueprintFull(string name)
    {
        using var cmd = _conn!.CreateCommand();
        cmd.CommandText = "SELECT id,name,category,sub_category FROM blueprints WHERE name=@n LIMIT 1";
        cmd.Parameters.AddWithValue("@n", name);
        using var rdr = cmd.ExecuteReader();
        if (!rdr.Read()) return null;
        var bp = new Blueprint
        {
            Name = rdr.GetString(1), Category = rdr.GetString(2),
            SubCategory = rdr.IsDBNull(3) ? null : rdr.GetString(3),
        };
        long bpId = rdr.GetInt64(0);
        rdr.Close();

        using var ingCmd = _conn.CreateCommand();
        ingCmd.CommandText = "SELECT resource_name,quantity,unit FROM blueprint_ingredients WHERE blueprint_id=@id ORDER BY resource_name";
        ingCmd.Parameters.AddWithValue("@id", bpId);
        using var ingRdr = ingCmd.ExecuteReader();
        while (ingRdr.Read())
            bp.Ingredients.Add(new BlueprintIngredient(ingRdr.GetString(0), ingRdr.GetDouble(1), ingRdr.GetString(2)));
        ingRdr.Close();

        using var unlockCmd = _conn.CreateCommand();
        unlockCmd.CommandText = "SELECT faction,mission_type,mission_title,rank,systems FROM blueprint_unlocks WHERE blueprint_name=@n ORDER BY faction,id";
        unlockCmd.Parameters.AddWithValue("@n", name);
        using var unlockRdr = unlockCmd.ExecuteReader();
        while (unlockRdr.Read())
        {
            var systems = unlockRdr.IsDBNull(4) ? null
                : unlockRdr.GetString(4).Split(',', StringSplitOptions.RemoveEmptyEntries);
            bp.UnlockEntries.Add(new BlueprintUnlockEntry(
                Faction:     unlockRdr.GetString(0),
                MissionType: unlockRdr.IsDBNull(1) ? null : unlockRdr.GetString(1),
                MissionTitle: unlockRdr.GetString(2),
                Rank:        unlockRdr.IsDBNull(3) ? null : unlockRdr.GetString(3),
                Systems:     systems
            ));
        }
        return bp;
    }

    // ── Work Orders ──────────────────────────────────────────────────────────

    public List<WorkOrder> GetWorkOrders()
    {
        var result = new List<WorkOrder>();
        using var cmd = _conn!.CreateCommand();
        cmd.CommandText = "SELECT id,label,resources,location,refinery,status,notes,created_at,timer_start,timer_end FROM work_orders ORDER BY created_at DESC";
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
        {
            result.Add(new WorkOrder
            {
                Id = rdr.GetString(0), Label = rdr.GetString(1), Resources = rdr.GetString(2),
                Location = rdr.GetString(3), Refinery = rdr.GetString(4),
                Status = (WorkOrderStatus)rdr.GetInt32(5), Notes = rdr.GetString(6),
                CreatedAt  = DateTime.Parse(rdr.GetString(7), null, System.Globalization.DateTimeStyles.RoundtripKind),
                TimerStart = rdr.IsDBNull(8) ? null : DateTime.Parse(rdr.GetString(8), null, System.Globalization.DateTimeStyles.RoundtripKind),
                TimerEnd   = rdr.IsDBNull(9) ? null : DateTime.Parse(rdr.GetString(9), null, System.Globalization.DateTimeStyles.RoundtripKind),
            });
        }
        return result;
    }

    public void SaveWorkOrder(WorkOrder wo)
    {
        Exec(@"INSERT OR REPLACE INTO work_orders(id,label,resources,location,refinery,status,notes,created_at,timer_start,timer_end) VALUES (@id,@l,@r,@loc,@ref,@s,@n,@ca,@ts,@te)",
            ("@id", wo.Id), ("@l", wo.Label), ("@r", wo.Resources),
            ("@loc", wo.Location), ("@ref", wo.Refinery), ("@s", (int)wo.Status),
            ("@n", wo.Notes), ("@ca", wo.CreatedAt.ToString("O")),
            ("@ts", (object?)wo.TimerStart?.ToString("O") ?? DBNull.Value),
            ("@te", (object?)wo.TimerEnd?.ToString("O") ?? DBNull.Value));
    }

    public void DeleteWorkOrder(string id) =>
        Exec("DELETE FROM work_orders WHERE id=@id", ("@id", id));

    // ── Shopping List ────────────────────────────────────────────────────────

    public List<ShoppingItem> GetShoppingList()
    {
        var result = new List<ShoppingItem>();
        using var cmd = _conn!.CreateCommand();
        cmd.CommandText = "SELECT resource_name,quantity,unit FROM shopping_list ORDER BY resource_name";
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
            result.Add(new ShoppingItem { ResourceName = rdr.GetString(0), Quantity = rdr.GetDouble(1), Unit = rdr.GetString(2) });
        return result;
    }

    public void AddToShoppingList(string resourceName, double qty, string unit)
    {
        Exec("INSERT INTO shopping_list VALUES (@r,@q,@u) ON CONFLICT(resource_name) DO UPDATE SET quantity=quantity+@q",
            ("@r", resourceName), ("@q", qty), ("@u", unit));
    }

    public void RemoveFromShoppingList(string resourceName) =>
        Exec("DELETE FROM shopping_list WHERE resource_name=@r", ("@r", resourceName));

    public void ClearShoppingList() => Exec("DELETE FROM shopping_list");

    public void ClearWorkOrders() => Exec("DELETE FROM work_orders");

    // ── Pinning ──────────────────────────────────────────────────────────────

    public void SetPinned(string resourceName, bool pinned) =>
        Exec("UPDATE resources SET is_pinned=@p WHERE name=@n", ("@p", pinned ? 1 : 0), ("@n", resourceName));

    public void ClearAllPins() => Exec("UPDATE resources SET is_pinned=0");

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void Exec(string sql, params (string, object)[] parms)
    {
        using var cmd = _conn!.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (k, v) in parms) cmd.Parameters.AddWithValue(k, v);
        cmd.ExecuteNonQuery();
    }

    private T Scalar<T>(string sql, params (string, object)[] parms)
    {
        using var cmd = _conn!.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (k, v) in parms) cmd.Parameters.AddWithValue(k, v);
        return (T)cmd.ExecuteScalar()!;
    }

    public void Dispose() => _conn?.Dispose();

    // ── Seed data DTOs ───────────────────────────────────────────────────────

    private record SeedData(
        string? MiningDataVersion,
        string? DataVersion,
        List<SeedResource> Resources,
        Dictionary<string, string> LocationSystems,
        List<SeedBlueprint> Blueprints,
        List<SeedBlueprintUnlock>? BlueprintUnlocks
    );

    private record SeedResource(
        string Name, int BaseRs, string Tier, string Rarity, string Method,
        List<string> Locations, List<SeedRefinery> Refineries,
        List<SeedFoundIn>? FoundIn
    );

    private record SeedRefinery(string Station, string System, int ModifierPct);

    private record SeedFoundIn(string Ore, double MinPct, double MaxPct, double Probability, int Variants);

    private record SeedBlueprint(
        string Name, string Category, string? SubCategory,
        List<SeedIngredient> Ingredients
    );

    private record SeedIngredient(string ResourceName, double Quantity, string Unit);

    private record SeedBlueprintUnlock(
        string BlueprintName, string Faction, string? MissionType,
        string MissionTitle, string? Rank, List<string>? Systems
    );
}

public record RsMatch(Resource Resource, int Nodes, bool IsExact, double ErrorPct);
