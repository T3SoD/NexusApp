using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;
using NexusApp.Models;

namespace NexusApp.Services;

/// <summary>
/// Storage for the Blueprint Network feature — other people's shared blueprint libraries and the
/// groups they belong to. Lives in its OWN SQLite file (network.db) so it survives the nexus.db
/// reseed that runs on every app update, and so the ~60k ownership rows at org scale stay indexed
/// and out of settings.json.
///
/// NOTE: the local user ("self") is NOT stored here — self's ownership is the single source of
/// truth in AppSettings.OwnedBlueprints. Coverage that includes self unions the two above this
/// layer. This store holds imported members only.
/// </summary>
public sealed class NetworkStore : IDisposable
{
    private static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "NexusApp", "network.db");

    private readonly SqliteConnection _conn;

    /// <param name="dbPath">Override the db location. Defaults to %AppData%\NexusApp\network.db.
    /// Tests pass a temp path so they run headless and never touch the real profile.</param>
    public NetworkStore(string? dbPath = null)
    {
        var path = dbPath ?? DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _conn = new SqliteConnection($"Data Source={path}");
        _conn.Open();
        Exec("PRAGMA journal_mode=WAL;");
        Exec("PRAGMA busy_timeout=3000;");
        CreateSchema();
    }

    private void CreateSchema() => Exec(@"
        CREATE TABLE IF NOT EXISTS members (
            id            TEXT PRIMARY KEY,
            display_name  TEXT NOT NULL,
            identity_kind TEXT NOT NULL,
            rsi_handle    TEXT,
            last_updated  TEXT NOT NULL,
            is_self       INTEGER NOT NULL DEFAULT 0
        );
        CREATE TABLE IF NOT EXISTS member_blueprints (
            member_id      TEXT NOT NULL,
            blueprint_name TEXT NOT NULL,
            PRIMARY KEY (member_id, blueprint_name)
        );
        CREATE INDEX IF NOT EXISTS ix_mb_blueprint ON member_blueprints(blueprint_name);
        CREATE INDEX IF NOT EXISTS ix_mb_member    ON member_blueprints(member_id);
        CREATE TABLE IF NOT EXISTS network_groups (
            id          TEXT PRIMARY KEY,
            name        TEXT NOT NULL,
            created_utc TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS group_members (
            group_id  TEXT NOT NULL,
            member_id TEXT NOT NULL,
            PRIMARY KEY (group_id, member_id)
        );
        CREATE INDEX IF NOT EXISTS ix_gm_group  ON group_members(group_id);
        CREATE INDEX IF NOT EXISTS ix_gm_member ON group_members(member_id);");

    // ── Members ─────────────────────────────────────────────────────────────────

    public int MemberCount
    {
        get
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM members;";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    /// <summary>Insert a member, or update it in place if its GUID already exists.</summary>
    public void UpsertMember(NetworkMember m)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO members (id, display_name, identity_kind, rsi_handle, last_updated, is_self)
            VALUES ($id, $name, $kind, $handle, $updated, $self)
            ON CONFLICT(id) DO UPDATE SET
                display_name = $name, identity_kind = $kind, rsi_handle = $handle,
                last_updated = $updated, is_self = $self;";
        cmd.Parameters.AddWithValue("$id", m.Id);
        cmd.Parameters.AddWithValue("$name", m.DisplayName);
        cmd.Parameters.AddWithValue("$kind", m.IdentityKind);
        cmd.Parameters.AddWithValue("$handle", (object?)m.RsiHandle ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$updated", m.LastUpdatedUtc.ToString("o", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$self", m.IsSelf ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    public NetworkMember? GetMember(string id)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT id, display_name, identity_kind, rsi_handle, last_updated, is_self FROM members WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadMember(r) : null;
    }

    /// <summary>Find a member by their RSI handle (case-insensitive). Used as the import fallback
    /// match when a re-installed user has lost their GUID but exported the same handle.</summary>
    public NetworkMember? FindByHandle(string handle)
    {
        if (string.IsNullOrWhiteSpace(handle)) return null;
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT id, display_name, identity_kind, rsi_handle, last_updated, is_self FROM members WHERE rsi_handle = $h COLLATE NOCASE LIMIT 1;";
        cmd.Parameters.AddWithValue("$h", handle);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadMember(r) : null;
    }

    public IReadOnlyList<NetworkMember> GetMembers()
    {
        var list = new List<NetworkMember>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT id, display_name, identity_kind, rsi_handle, last_updated, is_self FROM members ORDER BY display_name COLLATE NOCASE;";
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadMember(r));
        return list;
    }

    /// <summary>Remove a member and everything attached to them (ownership + group links).</summary>
    public void DeleteMember(string id)
    {
        using var tx = _conn.BeginTransaction();
        foreach (var sql in new[]
        {
            "DELETE FROM member_blueprints WHERE member_id = $id;",
            "DELETE FROM group_members WHERE member_id = $id;",
            "DELETE FROM members WHERE id = $id;",
        })
        {
            using var cmd = _conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    private static NetworkMember ReadMember(SqliteDataReader r) => new()
    {
        Id = r.GetString(0),
        DisplayName = r.GetString(1),
        IdentityKind = r.GetString(2),
        RsiHandle = r.IsDBNull(3) ? null : r.GetString(3),
        LastUpdatedUtc = DateTime.Parse(r.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        IsSelf = r.GetInt64(5) != 0,
    };

    // ── Ownership ───────────────────────────────────────────────────────────────

    /// <summary>Replace a member's entire owned-blueprint set (delete then insert) in one
    /// transaction — newer-wins import just hands us the new set.</summary>
    public void ReplaceOwnership(string memberId, IEnumerable<string> blueprintNames)
    {
        using var tx = _conn.BeginTransaction();
        using (var del = _conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM member_blueprints WHERE member_id = $m;";
            del.Parameters.AddWithValue("$m", memberId);
            del.ExecuteNonQuery();
        }
        using (var ins = _conn.CreateCommand())
        {
            ins.Transaction = tx;
            ins.CommandText = "INSERT OR IGNORE INTO member_blueprints (member_id, blueprint_name) VALUES ($m, $b);";
            var pm = ins.Parameters.Add("$m", SqliteType.Text);
            var pb = ins.Parameters.Add("$b", SqliteType.Text);
            pm.Value = memberId;
            foreach (var name in blueprintNames)
            {
                if (string.IsNullOrWhiteSpace(name)) continue;
                pb.Value = name;
                ins.ExecuteNonQuery();
            }
        }
        tx.Commit();
    }

    public IReadOnlyList<string> GetOwnedNames(string memberId)
    {
        var list = new List<string>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT blueprint_name FROM member_blueprints WHERE member_id = $m ORDER BY blueprint_name COLLATE NOCASE;";
        cmd.Parameters.AddWithValue("$m", memberId);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    /// <summary>How many stored members own this blueprint (case-insensitive). Excludes self.</summary>
    public int OwnerCount(string blueprintName)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(DISTINCT member_id) FROM member_blueprints WHERE blueprint_name = $b COLLATE NOCASE;";
        cmd.Parameters.AddWithValue("$b", blueprintName);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>blueprint name → number of stored members who own it. Pass a member-id set to scope
    /// the count to a group; null counts all stored members. Self is added by the layer above.</summary>
    public IReadOnlyDictionary<string, int> OwnerCounts(IReadOnlyCollection<string>? memberIds = null)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        using var cmd = _conn.CreateCommand();
        if (memberIds == null)
        {
            cmd.CommandText = "SELECT blueprint_name, COUNT(DISTINCT member_id) FROM member_blueprints GROUP BY blueprint_name COLLATE NOCASE;";
        }
        else
        {
            if (memberIds.Count == 0) return result;
            var placeholders = new List<string>();
            var i = 0;
            foreach (var id in memberIds)
            {
                var p = "$m" + i++;
                placeholders.Add(p);
                cmd.Parameters.AddWithValue(p, id);
            }
            cmd.CommandText =
                "SELECT blueprint_name, COUNT(DISTINCT member_id) FROM member_blueprints " +
                "WHERE member_id IN (" + string.Join(",", placeholders) + ") " +
                "GROUP BY blueprint_name COLLATE NOCASE;";
        }
        using var r = cmd.ExecuteReader();
        while (r.Read()) result[r.GetString(0)] = Convert.ToInt32(r.GetInt64(1));
        return result;
    }

    // ── Groups ──────────────────────────────────────────────────────────────────

    public NetworkGroup CreateGroup(string name)
    {
        var g = new NetworkGroup { Id = Guid.NewGuid().ToString(), Name = name, CreatedUtc = DateTime.UtcNow };
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "INSERT INTO network_groups (id, name, created_utc) VALUES ($id, $name, $created);";
        cmd.Parameters.AddWithValue("$id", g.Id);
        cmd.Parameters.AddWithValue("$name", g.Name);
        cmd.Parameters.AddWithValue("$created", g.CreatedUtc.ToString("o", CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();
        return g;
    }

    public IReadOnlyList<NetworkGroup> GetGroups()
    {
        var list = new List<NetworkGroup>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, created_utc FROM network_groups ORDER BY name COLLATE NOCASE;";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new NetworkGroup
            {
                Id = r.GetString(0),
                Name = r.GetString(1),
                CreatedUtc = DateTime.Parse(r.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            });
        return list;
    }

    public void RenameGroup(string groupId, string name)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "UPDATE network_groups SET name = $name WHERE id = $id;";
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$id", groupId);
        cmd.ExecuteNonQuery();
    }

    public void DeleteGroup(string groupId)
    {
        using var tx = _conn.BeginTransaction();
        foreach (var sql in new[]
        {
            "DELETE FROM group_members WHERE group_id = $id;",
            "DELETE FROM network_groups WHERE id = $id;",
        })
        {
            using var cmd = _conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$id", groupId);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public void AddToGroup(string groupId, string memberId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO group_members (group_id, member_id) VALUES ($g, $m);";
        cmd.Parameters.AddWithValue("$g", groupId);
        cmd.Parameters.AddWithValue("$m", memberId);
        cmd.ExecuteNonQuery();
    }

    public void RemoveFromGroup(string groupId, string memberId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM group_members WHERE group_id = $g AND member_id = $m;";
        cmd.Parameters.AddWithValue("$g", groupId);
        cmd.Parameters.AddWithValue("$m", memberId);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<string> GetGroupMemberIds(string groupId) =>
        QueryIds("SELECT member_id FROM group_members WHERE group_id = $p;", groupId);

    public IReadOnlyList<string> GetMemberGroupIds(string memberId) =>
        QueryIds("SELECT group_id FROM group_members WHERE member_id = $p;", memberId);

    private IReadOnlyList<string> QueryIds(string sql, string param)
    {
        var list = new List<string>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$p", param);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    private void Exec(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _conn.Dispose();
}
