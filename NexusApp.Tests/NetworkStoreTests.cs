using Microsoft.Data.Sqlite;
using NexusApp.Models;
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// Exercises NetworkStore against a temp network.db so it runs headless and never touches the
// real user profile. Mirrors the BlueprintOwnershipTests temp-file pattern.
public class NetworkStoreTests : IDisposable
{
    private readonly string _tempPath =
        Path.Combine(Path.GetTempPath(), $"nexus_net_{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();   // release the file handle so the temp db can be deleted
        foreach (var f in new[] { _tempPath, _tempPath + "-wal", _tempPath + "-shm" })
            try { if (File.Exists(f)) File.Delete(f); } catch { /* best-effort temp cleanup */ }
    }

    private static NetworkMember NewMember(string id, string name, string? handle = null) => new()
    {
        Id = id,
        DisplayName = name,
        IdentityKind = handle == null ? NetworkIdentityKind.Nickname : NetworkIdentityKind.Handle,
        RsiHandle = handle,
        LastUpdatedUtc = DateTime.UtcNow,
    };

    [Fact]
    public void NewStore_IsEmpty()
    {
        using var s = new NetworkStore(_tempPath);
        Assert.Equal(0, s.MemberCount);
    }

    [Fact]
    public void UpsertMember_RoundTripsAllFields()
    {
        using var s = new NetworkStore(_tempPath);
        var when = new DateTime(2026, 6, 22, 1, 2, 3, DateTimeKind.Utc);
        s.UpsertMember(new NetworkMember
        {
            Id = "g1", DisplayName = "Dave", IdentityKind = NetworkIdentityKind.Handle,
            RsiHandle = "DaveSC", LastUpdatedUtc = when,
        });

        var got = s.GetMember("g1");
        Assert.NotNull(got);
        Assert.Equal("Dave", got!.DisplayName);
        Assert.Equal(NetworkIdentityKind.Handle, got.IdentityKind);
        Assert.Equal("DaveSC", got.RsiHandle);
        Assert.Equal(when, got.LastUpdatedUtc);
    }

    [Fact]
    public void UpsertMember_SameId_UpdatesInPlace_NoDuplicate()
    {
        using var s = new NetworkStore(_tempPath);
        s.UpsertMember(NewMember("g1", "Dave"));
        s.UpsertMember(NewMember("g1", "David"));
        Assert.Equal(1, s.MemberCount);
        Assert.Equal("David", s.GetMember("g1")!.DisplayName);
    }

    [Fact]
    public void NicknameMember_HasNullHandle()
    {
        using var s = new NetworkStore(_tempPath);
        s.UpsertMember(NewMember("g1", "Anon"));   // nickname → no handle
        Assert.Null(s.GetMember("g1")!.RsiHandle);
    }

    [Fact]
    public void Ownership_ReplaceOverwrites_AndReads()
    {
        using var s = new NetworkStore(_tempPath);
        s.UpsertMember(NewMember("g1", "Dave"));
        s.ReplaceOwnership("g1", new[] { "Bracket Cooler", "Hellion Cannon" });
        Assert.Equal(2, s.GetOwnedNames("g1").Count);

        s.ReplaceOwnership("g1", new[] { "FR-86 Shield" });   // replaces, does not append
        var owned = s.GetOwnedNames("g1");
        Assert.Single(owned);
        Assert.Equal("FR-86 Shield", owned[0]);
    }

    [Fact]
    public void OwnerCount_CountsMembers_CaseInsensitively()
    {
        using var s = new NetworkStore(_tempPath);
        s.UpsertMember(NewMember("g1", "Dave"));
        s.UpsertMember(NewMember("g2", "Mara"));
        s.ReplaceOwnership("g1", new[] { "Bracket Cooler" });
        s.ReplaceOwnership("g2", new[] { "bracket cooler" });   // different casing, same blueprint

        Assert.Equal(2, s.OwnerCount("BRACKET COOLER"));
        var all = s.OwnerCounts();
        Assert.Single(all);
        Assert.Equal(2, all["Bracket Cooler"]);
    }

    [Fact]
    public void OwnerCounts_ScopedToMemberSubset()
    {
        using var s = new NetworkStore(_tempPath);
        s.UpsertMember(NewMember("g1", "Dave"));
        s.UpsertMember(NewMember("g2", "Mara"));
        s.ReplaceOwnership("g1", new[] { "Bracket Cooler" });
        s.ReplaceOwnership("g2", new[] { "Bracket Cooler" });

        Assert.Equal(1, s.OwnerCounts(new[] { "g1" })["Bracket Cooler"]);
        Assert.Empty(s.OwnerCounts(Array.Empty<string>()));   // empty scope → nothing
    }

    [Fact]
    public void FindByHandle_IsCaseInsensitive()
    {
        using var s = new NetworkStore(_tempPath);
        s.UpsertMember(NewMember("g1", "Dave", handle: "DaveSC"));
        Assert.Equal("g1", s.FindByHandle("davesc")!.Id);
        Assert.Null(s.FindByHandle("nobody"));
    }

    [Fact]
    public void DeleteMember_RemovesOwnershipAndGroupLinks()
    {
        using var s = new NetworkStore(_tempPath);
        s.UpsertMember(NewMember("g1", "Dave"));
        s.ReplaceOwnership("g1", new[] { "Bracket Cooler" });
        var grp = s.CreateGroup("Friends");
        s.AddToGroup(grp.Id, "g1");

        s.DeleteMember("g1");

        Assert.Equal(0, s.MemberCount);
        Assert.Empty(s.GetOwnedNames("g1"));
        Assert.Empty(s.GetGroupMemberIds(grp.Id));
    }

    [Fact]
    public void Groups_Create_Assign_Query_Delete()
    {
        using var s = new NetworkStore(_tempPath);
        s.UpsertMember(NewMember("g1", "Dave"));
        var friends = s.CreateGroup("Friends");
        var vanguard = s.CreateGroup("Vanguard");
        s.AddToGroup(friends.Id, "g1");
        s.AddToGroup(vanguard.Id, "g1");

        Assert.Equal(2, s.GetGroups().Count);
        Assert.Contains("g1", s.GetGroupMemberIds(friends.Id));
        Assert.Equal(2, s.GetMemberGroupIds("g1").Count);   // a member can be in several groups

        s.DeleteGroup(friends.Id);
        Assert.Single(s.GetGroups());
        Assert.Empty(s.GetGroupMemberIds(friends.Id));
        Assert.Single(s.GetMemberGroupIds("g1"));           // still in Vanguard
    }

    [Fact]
    public void Data_PersistsAcrossReopen()
    {
        using (var s = new NetworkStore(_tempPath))
        {
            s.UpsertMember(NewMember("g1", "Dave"));
            s.ReplaceOwnership("g1", new[] { "Bracket Cooler" });
        }
        using var reopened = new NetworkStore(_tempPath);
        Assert.Equal(1, reopened.MemberCount);
        Assert.Single(reopened.GetOwnedNames("g1"));
    }
}
