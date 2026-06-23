using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using NexusApp.Models;

namespace NexusApp.Services;

/// <summary>
/// Reads and writes .nexuslib files and applies an imported file to a <see cref="NetworkStore"/>.
/// All network traffic is the user moving a file by hand — this service never touches the network.
/// Pure/headless: file I/O and store access are the only side effects, both injectable for tests.
/// </summary>
public sealed class NetworkFileService
{
    public const int CurrentSchema = 1;

    /// <summary>Label written into the "app" field of exports (informational only). The UI sets it
    /// to include the version; defaults keep the service decoupled from AppInfo for tests.</summary>
    public string AppLabel { get; set; } = "NexusApp";

    private static readonly JsonSerializerOptions _opts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // ── Build ─────────────────────────────────────────────────────────────────

    /// <summary>Build a single-member library file representing the local user.</summary>
    public NetworkFile BuildSelfLibrary(string selfId, string displayName, string identityKind,
        string? rsiHandle, IEnumerable<string> ownedBlueprints, DateTime nowUtc)
    {
        var kind = NormalizeKind(identityKind);
        return new NetworkFile
        {
            Schema = CurrentSchema,
            Kind = NetworkFileKind.Library,
            ExportedAtUtc = Iso(nowUtc),
            App = AppLabel,
            Member = new NetworkFileMember
            {
                Id = selfId,
                Name = displayName,
                IdentityKind = kind,
                RsiHandle = kind == NetworkIdentityKind.Handle ? rsiHandle : null,
                UpdatedAtUtc = Iso(nowUtc),
                OwnedBlueprints = new List<string>(ownedBlueprints),
            },
        };
    }

    /// <summary>Build a combined roster file (coordinator export) from already-assembled members.</summary>
    public NetworkFile BuildRoster(string? groupName, IEnumerable<NetworkFileMember> members, DateTime nowUtc) => new()
    {
        Schema = CurrentSchema,
        Kind = NetworkFileKind.Roster,
        ExportedAtUtc = Iso(nowUtc),
        App = AppLabel,
        GroupName = string.IsNullOrWhiteSpace(groupName) ? null : groupName,
        Members = new List<NetworkFileMember>(members),
    };

    /// <summary>Map a stored member + its owned set into the file shape (for roster export).</summary>
    public static NetworkFileMember ToFileMember(NetworkMember m, IEnumerable<string> owned) => new()
    {
        Id = m.Id,
        Name = m.DisplayName,
        IdentityKind = m.IdentityKind,
        RsiHandle = m.IdentityKind == NetworkIdentityKind.Handle ? m.RsiHandle : null,
        UpdatedAtUtc = Iso(m.LastUpdatedUtc),
        OwnedBlueprints = new List<string>(owned),
    };

    // ── Serialize / parse ───────────────────────────────────────────────────────

    public string Serialize(NetworkFile file) => JsonSerializer.Serialize(file, _opts);

    public void Save(string path, NetworkFile file) => File.WriteAllText(path, Serialize(file));

    public NetworkFile Load(string path) => Parse(File.ReadAllText(path));

    /// <summary>Parse + validate a .nexuslib payload. Throws <see cref="NetworkFileException"/> on
    /// anything malformed or made by a newer Nexus.</summary>
    public NetworkFile Parse(string json)
    {
        NetworkFile? file;
        try { file = JsonSerializer.Deserialize<NetworkFile>(json, _opts); }
        catch (JsonException ex) { throw new NetworkFileException("This file isn't a valid Nexus library file.", ex); }

        if (file == null) throw new NetworkFileException("This file is empty or unreadable.");
        if (file.Schema > CurrentSchema)
            throw new NetworkFileException("This file was made by a newer version of Nexus. Update Nexus to import it.", unsupportedNewerVersion: true);
        if (file.Schema < 1)
            throw new NetworkFileException("This file is missing its version and can't be read.");

        var kind = file.Kind?.ToLowerInvariant() ?? "";
        if (kind == NetworkFileKind.Library)
        {
            if (file.Member == null) throw new NetworkFileException("This library file has no member data.");
        }
        else if (kind == NetworkFileKind.Roster)
        {
            if (file.Members == null) throw new NetworkFileException("This roster file has no members.");
        }
        else throw new NetworkFileException("Unrecognized file type.");

        file.Kind = kind;   // normalize for downstream comparisons
        return file;
    }

    // ── Import ──────────────────────────────────────────────────────────────────

    /// <summary>Apply a parsed file to the store: match each member by GUID, then by RSI handle,
    /// else add new; newer-wins on the owned set; skip the local user's own entry; auto-assign to
    /// the file's (or options') group.</summary>
    public ImportResult Import(NetworkFile file, NetworkStore store, ImportOptions options)
    {
        var result = new ImportResult();
        var incoming = MembersOf(file);

        var groupName = string.Equals(file.Kind, NetworkFileKind.Roster, StringComparison.OrdinalIgnoreCase)
            ? file.GroupName
            : options.AssignToGroupName;
        NetworkGroup? group = string.IsNullOrWhiteSpace(groupName) ? null : FindOrCreateGroup(store, groupName!);
        result.GroupName = group?.Name;

        var known = options.KnownBlueprints != null
            ? new HashSet<string>(options.KnownBlueprints, StringComparer.OrdinalIgnoreCase)
            : null;
        var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var inc in incoming)
        {
            if (inc == null || string.IsNullOrWhiteSpace(inc.Id)) continue;

            if (IsSelf(inc, options))
            {
                result.SkippedSelf++;
                if (inc.OwnedBlueprints != null) result.SelfBlueprints.AddRange(inc.OwnedBlueprints);
                continue;
            }

            var match = store.GetMember(inc.Id);
            if (match == null
                && string.Equals(inc.IdentityKind, NetworkIdentityKind.Handle, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(inc.RsiHandle))
            {
                match = store.FindByHandle(inc.RsiHandle!);
            }

            var incUpdated = ParseUtc(inc.UpdatedAtUtc);
            var owned = inc.OwnedBlueprints ?? new List<string>();

            if (match == null)
            {
                store.UpsertMember(MakeMember(inc.Id, inc, incUpdated));
                store.ReplaceOwnership(inc.Id, owned);
                if (group != null) store.AddToGroup(group.Id, inc.Id);
                result.NewMembers++;
                AddNames(touched, owned);
            }
            else if (incUpdated > match.LastUpdatedUtc)
            {
                store.UpsertMember(MakeMember(match.Id, inc, incUpdated));   // keep the existing canonical id
                store.ReplaceOwnership(match.Id, owned);
                if (group != null) store.AddToGroup(group.Id, match.Id);
                result.UpdatedMembers++;
                AddNames(touched, owned);
            }
            else
            {
                result.SkippedOlder++;
                if (group != null) store.AddToGroup(group.Id, match.Id);   // still affirm group membership
            }
        }

        if (known != null)
            foreach (var n in touched)
                if (known.Contains(n)) result.BlueprintsMatched++; else result.BlueprintsUnrecognized++;
        else
            result.BlueprintsMatched = touched.Count;

        return result;
    }

    /// <summary>Incoming would-be-new members whose display name collides with an existing member of
    /// a different id — the UI prompts "new person, or merge?" before importing.</summary>
    public IReadOnlyList<NameCollision> DetectCollisions(NetworkFile file, NetworkStore store, string? selfId = null)
    {
        var collisions = new List<NameCollision>();
        var existing = store.GetMembers();
        foreach (var inc in MembersOf(file))
        {
            if (inc == null || string.IsNullOrWhiteSpace(inc.Id)) continue;
            if (selfId != null && string.Equals(inc.Id, selfId, StringComparison.OrdinalIgnoreCase)) continue;
            if (store.GetMember(inc.Id) != null) continue;   // matches by id → it's an update, not a collision
            if (string.Equals(inc.IdentityKind, NetworkIdentityKind.Handle, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(inc.RsiHandle)
                && store.FindByHandle(inc.RsiHandle!) != null) continue;   // matches by handle → update

            var clash = existing.FirstOrDefault(m =>
                m.Id != inc.Id && string.Equals(m.DisplayName, inc.Name, StringComparison.OrdinalIgnoreCase));
            if (clash != null)
                collisions.Add(new NameCollision { IncomingId = inc.Id, ExistingId = clash.Id, Name = inc.Name });
        }
        return collisions;
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static List<NetworkFileMember> MembersOf(NetworkFile file)
    {
        if (string.Equals(file.Kind, NetworkFileKind.Roster, StringComparison.OrdinalIgnoreCase))
            return file.Members ?? new List<NetworkFileMember>();
        return file.Member is null ? new List<NetworkFileMember>() : new List<NetworkFileMember> { file.Member };
    }

    private static bool IsSelf(NetworkFileMember inc, ImportOptions o) =>
        (!string.IsNullOrWhiteSpace(o.SelfId) && string.Equals(inc.Id, o.SelfId, StringComparison.OrdinalIgnoreCase))
        || (!string.IsNullOrWhiteSpace(o.SelfHandle)
            && string.Equals(inc.IdentityKind, NetworkIdentityKind.Handle, StringComparison.OrdinalIgnoreCase)
            && string.Equals(inc.RsiHandle, o.SelfHandle, StringComparison.OrdinalIgnoreCase));

    private static NetworkMember MakeMember(string id, NetworkFileMember inc, DateTime updatedUtc) => new()
    {
        Id = id,
        DisplayName = inc.Name,
        IdentityKind = NormalizeKind(inc.IdentityKind),
        RsiHandle = inc.RsiHandle,
        LastUpdatedUtc = updatedUtc,
        IsSelf = false,
    };

    private static NetworkGroup FindOrCreateGroup(NetworkStore store, string name)
    {
        foreach (var g in store.GetGroups())
            if (string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase)) return g;
        return store.CreateGroup(name);
    }

    private static void AddNames(HashSet<string> set, IEnumerable<string> names)
    {
        foreach (var n in names) if (!string.IsNullOrWhiteSpace(n)) set.Add(n);
    }

    private static string NormalizeKind(string? kind) =>
        string.Equals(kind, NetworkIdentityKind.Nickname, StringComparison.OrdinalIgnoreCase)
            ? NetworkIdentityKind.Nickname : NetworkIdentityKind.Handle;

    private static string Iso(DateTime utc) => utc.ToString("o", CultureInfo.InvariantCulture);

    private static DateTime ParseUtc(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return DateTime.MinValue;
        if (!DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt))
            return DateTime.MinValue;
        return dt.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : dt.ToUniversalTime();
    }
}

/// <summary>Outcome of an import, for the summary dialog and (counts-only) logging.</summary>
public sealed class ImportResult
{
    public int NewMembers { get; set; }
    public int UpdatedMembers { get; set; }
    public int SkippedOlder { get; set; }
    public int SkippedSelf { get; set; }
    public int BlueprintsMatched { get; set; }
    public int BlueprintsUnrecognized { get; set; }
    public string? GroupName { get; set; }
    /// <summary>Owned blueprints from your own entry in the file (matched as self) — the caller marks
    /// these Owned in your local library so importing your own export syncs your collection.</summary>
    public List<string> SelfBlueprints { get; } = new();
    public int TotalApplied => NewMembers + UpdatedMembers;
}

public sealed class ImportOptions
{
    /// <summary>The local user's GUID — their own entry in a roster is skipped (self comes from settings).</summary>
    public string? SelfId { get; init; }
    /// <summary>The local user's RSI handle — also used to recognise their own entry.</summary>
    public string? SelfHandle { get; init; }
    /// <summary>For a single-library import, the group to file the new member under. A roster uses its
    /// own embedded group name instead.</summary>
    public string? AssignToGroupName { get; init; }
    /// <summary>Known blueprint names (from the local seed) — lets the result report matched vs
    /// unrecognized. Compared case-insensitively regardless of the collection passed.</summary>
    public IReadOnlyCollection<string>? KnownBlueprints { get; init; }
}

public sealed class NameCollision
{
    public string IncomingId { get; init; } = "";
    public string ExistingId { get; init; } = "";
    public string Name { get; init; } = "";
}

public sealed class NetworkFileException : Exception
{
    public bool UnsupportedNewerVersion { get; }
    public NetworkFileException(string message, bool unsupportedNewerVersion = false) : base(message)
        => UnsupportedNewerVersion = unsupportedNewerVersion;
    public NetworkFileException(string message, Exception inner) : base(message, inner) { }
}
