using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// Exercises the headless session logic of GameLogSession (auto-mark gating, dedup, the
// per-session tally) by feeding lines straight into Ingest with injected ownership fakes —
// no WPF window and no real Game.log file.
public class GameLogSessionTests
{
    private static GameLogSession Make(out HashSet<string> owned, params string[] knownNames)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        owned = set;
        return new GameLogSession(
            () => knownNames,
            name => set.Contains(name),
            (name, isOwned) => { if (isOwned) set.Add(name); else set.Remove(name); });
    }

    private static GameLogEntry Bp(string name) =>
        new() { Raw = $"<2026.01.01-12.00.00> [Notice] Received Blueprint: {name}", Category = LogCategory.Blueprint };

    [Fact]
    public void AutoMarkOff_DoesNotMark()
    {
        var s = Make(out var owned, "Bracket Cooler");
        s.Ingest(Bp("Bracket Cooler"));
        Assert.Equal(0, s.Count);
        Assert.Empty(owned);
    }

    [Fact]
    public void AutoMarkOn_MarksKnownBlueprint_RecordedOnce()
    {
        var s = Make(out var owned, "Bracket Cooler");
        s.SetAutoMark(true);
        s.Ingest(Bp("Bracket Cooler"));
        Assert.Equal(1, s.Count);
        Assert.Equal("Bracket Cooler", s.Marks[0].Name);
        Assert.Contains("Bracket Cooler", owned);
    }

    [Fact]
    public void DuplicateReceipt_RecordedOnlyOnce()
    {
        var s = Make(out _, "Bracket Cooler");
        s.SetAutoMark(true);
        s.Ingest(Bp("Bracket Cooler"));
        s.Ingest(Bp("Bracket Cooler"));
        Assert.Equal(1, s.Count);
    }

    [Fact]
    public void AlreadyOwned_NotRecordedInSession()
    {
        var s = Make(out var owned, "Bracket Cooler");
        owned.Add("Bracket Cooler");
        s.SetAutoMark(true);
        s.Ingest(Bp("Bracket Cooler"));
        Assert.Equal(0, s.Count);
    }

    [Fact]
    public void UnknownBlueprint_NotMarked()
    {
        var s = Make(out var owned, "Bracket Cooler");
        s.SetAutoMark(true);
        s.Ingest(Bp("Totally Unknown Part"));
        Assert.Equal(0, s.Count);
        Assert.Empty(owned);
    }

    [Fact]
    public void NonBlueprintLine_Ignored()
    {
        var s = Make(out _, "Bracket Cooler");
        s.SetAutoMark(true);
        s.Ingest(new GameLogEntry { Raw = "Received Blueprint: Bracket Cooler", Category = LogCategory.Quantum });
        Assert.Equal(0, s.Count);
    }

    [Fact]
    public void MarkedEvent_FiresOncePerNewBlueprint()
    {
        var s = Make(out _, "Bracket Cooler", "Hellion Cannon");
        s.SetAutoMark(true);
        int fired = 0;
        s.Marked += _ => fired++;
        s.Ingest(Bp("Bracket Cooler"));
        s.Ingest(Bp("Hellion Cannon"));
        s.Ingest(Bp("Bracket Cooler"));   // duplicate — should not re-fire
        Assert.Equal(2, fired);
        Assert.Equal(2, s.Count);
    }

    [Fact]
    public void Marks_AreInChronologicalOrder()
    {
        var s = Make(out _, "Bracket Cooler", "Hellion Cannon");
        s.SetAutoMark(true);
        s.Ingest(Bp("Bracket Cooler"));
        s.Ingest(Bp("Hellion Cannon"));
        Assert.Equal("Bracket Cooler", s.Marks[0].Name);
        Assert.Equal("Hellion Cannon", s.Marks[1].Name);
    }
}
