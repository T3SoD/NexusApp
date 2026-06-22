using Microsoft.Data.Sqlite;
using NexusApp.Models;
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// Exercises .nexuslib serialize/parse and the import matching/merge logic against a temp
// network.db, fully headless.
public class NetworkFileServiceTests : IDisposable
{
    private readonly string _tempPath =
        Path.Combine(Path.GetTempPath(), $"nexus_file_{Guid.NewGuid():N}.db");
    private readonly NetworkFileService _svc = new();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _tempPath, _tempPath + "-wal", _tempPath + "-shm" })
            try { if (File.Exists(f)) File.Delete(f); } catch { /* best-effort temp cleanup */ }
    }

    private NetworkStore Store() => new(_tempPath);

    private static readonly DateTime T1 = new(2026, 6, 22, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime T2 = new(2026, 6, 22, 1, 0, 0, DateTimeKind.Utc);

    private NetworkFile Library(string id, string name, string? handle, DateTime when, params string[] owned) =>
        _svc.BuildSelfLibrary(id, name, handle == null ? NetworkIdentityKind.Nickname : NetworkIdentityKind.Handle,
            handle, owned, when);

    private static NetworkFileMember FileMember(string id, string name, params string[] owned) => new()
    {
        Id = id, Name = name, IdentityKind = NetworkIdentityKind.Handle, RsiHandle = name,
        UpdatedAtUtc = T1.ToString("o"), OwnedBlueprints = new List<string>(owned),
    };

    [Fact]
    public void SelfLibrary_RoundTrips_AsCamelCase_WithoutNulls()
    {
        var file = Library("g1", "PlayerName", "PlayerName", T1, "Bracket Cooler", "Hellion Cannon");
        var json = _svc.Serialize(file);

        Assert.Contains("\"kind\": \"library\"", json);    // camelCase keys/values
        Assert.DoesNotContain("\"members\"", json);        // null roster array omitted

        var parsed = _svc.Parse(json);
        Assert.Equal(NetworkFileKind.Library, parsed.Kind);
        Assert.Equal("g1", parsed.Member!.Id);
        Assert.Equal(2, parsed.Member.OwnedBlueprints.Count);
    }

    [Fact]
    public void Parse_InvalidJson_Throws()
    {
        Assert.Throws<NetworkFileException>(() => _svc.Parse("{ not json"));
    }

    [Fact]
    public void Parse_NewerSchema_ThrowsUnsupported()
    {
        var json = _svc.Serialize(new NetworkFile
        {
            Schema = 99, Kind = NetworkFileKind.Library,
            Member = new NetworkFileMember { Id = "g1", Name = "x" },
        });
        var ex = Assert.Throws<NetworkFileException>(() => _svc.Parse(json));
        Assert.True(ex.UnsupportedNewerVersion);
    }

    [Fact]
    public void Import_NewMember_AddsToStore()
    {
        using var store = Store();
        var res = _svc.Import(Library("g1", "Dave", "DaveSC", T1, "Bracket Cooler"), store, new ImportOptions());
        Assert.Equal(1, res.NewMembers);
        Assert.Equal(1, store.MemberCount);
        Assert.Single(store.GetOwnedNames("g1"));
    }

    [Fact]
    public void Import_SameId_NewerUpdates_OlderSkips()
    {
        using var store = Store();
        _svc.Import(Library("g1", "Dave", "DaveSC", T1, "A"), store, new ImportOptions());

        var newer = _svc.Import(Library("g1", "Dave", "DaveSC", T2, "B", "C"), store, new ImportOptions());
        Assert.Equal(1, newer.UpdatedMembers);
        Assert.Equal(2, store.GetOwnedNames("g1").Count);
        Assert.Contains("B", store.GetOwnedNames("g1"));

        var older = _svc.Import(Library("g1", "Dave", "DaveSC", T1, "Z"), store, new ImportOptions());
        Assert.Equal(1, older.SkippedOlder);
        Assert.Equal(2, store.GetOwnedNames("g1").Count);   // unchanged by the stale file
    }

    [Fact]
    public void Import_MatchesByHandle_WhenGuidDiffers()
    {
        using var store = Store();
        _svc.Import(Library("old-guid", "Dave", "DaveSC", T1, "A"), store, new ImportOptions());

        // Reinstalled: brand-new GUID, same handle, newer file.
        var r = _svc.Import(Library("new-guid", "Dave", "DaveSC", T2, "A", "B"), store, new ImportOptions());
        Assert.Equal(1, r.UpdatedMembers);
        Assert.Equal(1, store.MemberCount);                       // matched by handle, no duplicate
        Assert.Equal(2, store.GetOwnedNames("old-guid").Count);   // updated under the original id
    }

    [Fact]
    public void Import_SkipsSelf_ById()
    {
        using var store = Store();
        var r = _svc.Import(Library("me", "PlayerName", "PlayerName", T1, "A"), store, new ImportOptions { SelfId = "me" });
        Assert.Equal(1, r.SkippedSelf);
        Assert.Equal(0, store.MemberCount);
    }

    [Fact]
    public void Import_Roster_CreatesGroup_AndAssignsMembers()
    {
        using var store = Store();
        var roster = _svc.BuildRoster("Vanguard Industries",
            new[] { FileMember("g1", "Dave", "A"), FileMember("g2", "Mara", "B") }, T1);

        var r = _svc.Import(roster, store, new ImportOptions());
        Assert.Equal(2, r.NewMembers);
        Assert.Equal("Vanguard Industries", r.GroupName);

        var grp = Assert.Single(store.GetGroups());
        Assert.Equal(2, store.GetGroupMemberIds(grp.Id).Count);
    }

    [Fact]
    public void Import_TalliesMatchedVsUnrecognized()
    {
        using var store = Store();
        var known = new HashSet<string>(new[] { "Bracket Cooler" }, StringComparer.OrdinalIgnoreCase);
        var r = _svc.Import(Library("g1", "Dave", "DaveSC", T1, "Bracket Cooler", "Mystery Widget"),
            store, new ImportOptions { KnownBlueprints = known });
        Assert.Equal(1, r.BlueprintsMatched);
        Assert.Equal(1, r.BlueprintsUnrecognized);
    }

    [Fact]
    public void DetectCollisions_FindsSameNameDifferentId()
    {
        using var store = Store();
        _svc.Import(Library("g1", "Dave", null, T1, "A"), store, new ImportOptions());   // nickname, no handle

        var incoming = Library("g2", "Dave", null, T1, "B");
        var collisions = _svc.DetectCollisions(incoming, store);
        var clash = Assert.Single(collisions);
        Assert.Equal("g1", clash.ExistingId);
        Assert.Equal("g2", clash.IncomingId);
    }
}
