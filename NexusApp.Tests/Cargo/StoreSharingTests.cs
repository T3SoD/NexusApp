using System.IO;
using NexusApp.Models.Cargo;
using NexusApp.Services.Cargo;
using Xunit;

namespace NexusApp.Tests.Cargo;

// The Cargo Planner and Grid Studio formerly each held a load-once snapshot of these stores, so a save
// in one view was invisible to the other until an app restart. LoadDefault now shares one instance and
// Reload re-reads the file (for the cross-process case). These lock both behaviors.
public class StoreSharingTests
{
    private static string TempFile() => Path.Combine(Path.GetTempPath(), $"store_{Guid.NewGuid():N}.json");

    [Fact]
    public void OverrideStore_Reload_PicksUpAnotherWritersChange()
    {
        var path = TempFile();
        try
        {
            var a = new CargoGridOverrideStore(path);
            var b = new CargoGridOverrideStore(path);
            Assert.False(b.Has("crus-c1-spirit"));

            a.Set("crus-c1-spirit", "C1 Spirit", new List<GridOverride>
            {
                new() { Id = 0, W = 4, D = 2, H = 2, Cap = 8, Accepts = new List<int> { 8 } },
            });

            Assert.False(b.Has("crus-c1-spirit"));   // load-once snapshot has not seen it yet
            b.Reload();
            Assert.True(b.Has("crus-c1-spirit"));
            Assert.Single(b.Get("crus-c1-spirit")!);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void SignoffStore_Reload_PicksUpAnotherWritersChange()
    {
        var path = TempFile();
        try
        {
            var a = new CargoSignoffStore(path);
            var b = new CargoSignoffStore(path);

            a.SetCheck("raft", "RAFT", CargoSignoffStore.ReviewItem.Hologram, true);
            Assert.False(b.IsChecked("raft", CargoSignoffStore.ReviewItem.Hologram));

            b.Reload();
            Assert.True(b.IsChecked("raft", CargoSignoffStore.ReviewItem.Hologram));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void LoadDefault_ReturnsOneSharedInstance()
    {
        Assert.Same(CargoGridOverrideStore.LoadDefault(), CargoGridOverrideStore.LoadDefault());
        Assert.Same(CargoSignoffStore.LoadDefault(), CargoSignoffStore.LoadDefault());
    }

    [Fact]
    public void OverrideStore_Set_ReturnsFalseAndRollsBackOnWriteFailure()
    {
        // A path under a directory that does not exist makes the atomic write throw; Set must report the
        // failure and leave the in-memory state matching disk (empty), not a phantom entry.
        var path = Path.Combine(Path.GetTempPath(), $"nx_{Guid.NewGuid():N}", "sub", "overrides.json");
        var store = new CargoGridOverrideStore(path);

        bool ok = store.Set("crus-c1-spirit", "C1 Spirit", new List<GridOverride>
        {
            new() { Id = 0, W = 4, D = 2, H = 2, Cap = 8, Accepts = new List<int> { 8 } },
        });

        Assert.False(ok);
        Assert.False(store.Has("crus-c1-spirit"));
    }

    [Fact]
    public void SignoffStore_SetCheck_ReturnsFalseAndRollsBackOnWriteFailure()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nx_{Guid.NewGuid():N}", "sub", "signoff.json");
        var store = new CargoSignoffStore(path);

        bool ok = store.SetCheck("raft", "RAFT", CargoSignoffStore.ReviewItem.Hologram, true);

        Assert.False(ok);
        Assert.False(store.IsChecked("raft", CargoSignoffStore.ReviewItem.Hologram));
    }

    [Fact]
    public void Signoff_IsStale_WhenReviewedFingerprintChanges()
    {
        var path = TempFile();
        try
        {
            var s = new CargoSignoffStore(path);
            s.SetCheck("raft", "RAFT", CargoSignoffStore.ReviewItem.GridPosition, true, "fp-original");
            Assert.False(s.IsStale("raft", "fp-original"));
            Assert.True(s.IsStale("raft", "fp-changed"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Signoff_WithoutFingerprint_IsNeverStale()
    {
        var path = TempFile();
        try
        {
            var s = new CargoSignoffStore(path);
            s.SetCheck("raft", "RAFT", CargoSignoffStore.ReviewItem.GridPosition, true);   // no fingerprint recorded
            Assert.False(s.IsStale("raft", "anything"));
        }
        finally { File.Delete(path); }
    }
}
