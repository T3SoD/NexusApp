using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// Issue #48: the Game.log slice a user attaches to a bug report.
public class GameLogExportTests
{
    private static string Line(string hhmmss, string body = "<Legacy login response> ok") =>
        $"<2026-08-10T{hhmmss}.000Z> {body}";

    private static DateTime Utc(string hhmmss) =>
        DateTime.Parse($"2026-08-10T{hhmmss}.000Z").ToUniversalTime();

    // ---- StampOf ------------------------------------------------------------------------------

    [Fact]
    public void StampOf_ReadsTheLeadingUtcStamp()
        => Assert.Equal(Utc("12:00:00"), GameLogExport.StampOf(Line("12:00:00")));

    [Fact]
    public void StampOf_IsNullForAContinuationLine()
        => Assert.Null(GameLogExport.StampOf("    at SomeMethod(...)"));

    // A timestamp-shaped string later in a line is DATA (a mission expiry, a quoted log echo), not
    // the line's own time. The pattern is anchored so it can never be mistaken for one.
    [Fact]
    public void StampOf_IgnoresATimestampThatIsNotAtTheStart()
        => Assert.Null(GameLogExport.StampOf("expires at <2026-08-10T12:00:00.000Z>"));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void StampOf_HandlesNothing(string? line) => Assert.Null(GameLogExport.StampOf(line));

    // ---- range filtering ----------------------------------------------------------------------

    private static readonly string[] Sample =
    {
        Line("10:00:00", "early"),
        Line("12:00:00", "middle"),
        Line("14:00:00", "late"),
    };

    [Fact]
    public void NoBounds_KeepsEverything()
    {
        var r = GameLogExport.Build(Sample, null, null, false, null, null, "6.13.4", DateTime.UtcNow);
        Assert.Equal(3, r.LinesKept);
        Assert.Equal(3, r.LinesTotal);
    }

    [Fact]
    public void RangeIsInclusiveAtBothEnds()
    {
        var r = GameLogExport.Build(Sample, Utc("10:00:00"), Utc("14:00:00"), false, null, null, "v", DateTime.UtcNow);
        Assert.Equal(3, r.LinesKept);
    }

    [Fact]
    public void LinesOutsideTheRangeAreDropped()
    {
        var r = GameLogExport.Build(Sample, Utc("11:00:00"), Utc("13:00:00"), false, null, null, "v", DateTime.UtcNow);
        Assert.Equal(1, r.LinesKept);
        Assert.Contains("middle", r.Text);
        Assert.DoesNotContain("early", r.Text);
        Assert.DoesNotContain("late", r.Text);
    }

    [Fact]
    public void OnlyALowerBound_KeepsEverythingAfterIt()
    {
        var r = GameLogExport.Build(Sample, Utc("12:00:00"), null, false, null, null, "v", DateTime.UtcNow);
        Assert.Equal(2, r.LinesKept);
    }

    // A stack trace belongs to the entry above it. Dropping continuation lines would hand the
    // maintainer an exception with no trace, which is the half that matters.
    [Fact]
    public void ContinuationLines_TravelWithTheEntryTheyBelongTo()
    {
        var lines = new[]
        {
            Line("10:00:00", "early"),
            Line("12:00:00", "Exception!"),
            "   at Frame.One()",
            "   at Frame.Two()",
            Line("14:00:00", "late"),
        };
        var r = GameLogExport.Build(lines, Utc("11:00:00"), Utc("13:00:00"), false, null, null, "v", DateTime.UtcNow);
        Assert.Equal(3, r.LinesKept);
        Assert.Contains("Frame.Two", r.Text);
    }

    [Fact]
    public void ContinuationLinesOfADroppedEntry_AreDroppedToo()
    {
        var lines = new[] { Line("10:00:00", "early"), "   at Frame.One()", Line("12:00:00", "middle") };
        var r = GameLogExport.Build(lines, Utc("11:00:00"), null, false, null, null, "v", DateTime.UtcNow);
        Assert.Equal(1, r.LinesKept);
        Assert.DoesNotContain("Frame.One", r.Text);
    }

    // Lines before the file's first stamp cannot be placed in time. With a lower bound they are
    // excluded (they might predate it); with no lower bound nothing is being excluded at all.
    [Fact]
    public void UnplaceableLeadingLines_AreKeptOnlyWhenNothingIsExcluded()
    {
        var lines = new[] { "header line", Line("12:00:00", "middle") };
        Assert.Equal(2, GameLogExport.Build(lines, null, null, false, null, null, "v", DateTime.UtcNow).LinesKept);
        Assert.Equal(1, GameLogExport.Build(lines, Utc("11:00:00"), null, false, null, null, "v", DateTime.UtcNow).LinesKept);
    }

    // ---- scrubbing ----------------------------------------------------------------------------

    [Fact]
    public void Scrub_RemovesTheHandleField()
        => Assert.Equal("User Login Success - Handle[<REDACTED>] - Time[x]",
            GameLogExport.Scrub("User Login Success - Handle[CmdrJones] - Time[x]", null, null));

    [Fact]
    public void Scrub_RemovesTheCharacterNameField()
        => Assert.Contains("- name <REDACTED> - state",
            GameLogExport.Scrub("AccountLoginCharacterStatus_Character Character: - name CmdrJones - state STATE_CURRENT", null, null));

    [Fact]
    public void Scrub_RemovesPlayerAndAccountIds()
    {
        var s = GameLogExport.Scrub("playerId[123456] accountId[abc] geid[999]", null, null);
        Assert.Equal("playerId[<REDACTED>] accountId[<REDACTED>] geid[<REDACTED>]", s);
    }

    [Fact]
    public void Scrub_RemovesIpAddresses()
        => Assert.Equal("connect to <IP>:64090", GameLogExport.Scrub("connect to 203.0.113.42:64090", null, null));

    [Fact]
    public void Scrub_RemovesTheHandleWhereverItAppearsLoose()
        => Assert.Equal("<REDACTED> joined the party",
            GameLogExport.Scrub("CmdrJones joined the party", "CmdrJones", null));

    [Fact]
    public void Scrub_MatchesTheHandleRegardlessOfCase()
        => Assert.Equal("<REDACTED> joined", GameLogExport.Scrub("cmdrjones joined", "CmdrJones", null));

    // The Windows user folder can be a real name, so it is redacted the same way the diagnostic
    // snapshot already redacts it. One rule, one implementation.
    [Fact]
    public void Scrub_RedactsTheUserProfilePath()
        => Assert.Contains("%USERPROFILE%",
            GameLogExport.Scrub(@"loading C:\Users\realname\StarCitizen", null, @"C:\Users\realname"));

    [Fact]
    public void Scrub_LeavesAnOrdinaryLineAlone()
    {
        const string line = "<2026-08-10T12:00:00.000Z> [Notice] CEntityComponentCommodityUIProvider bought";
        Assert.Equal(line, GameLogExport.Scrub(line, "CmdrJones", @"C:\Users\realname"));
    }

    [Fact]
    public void ScrubOff_LeavesThePiiInPlace()
    {
        var lines = new[] { Line("12:00:00", "Handle[CmdrJones]") };
        var r = GameLogExport.Build(lines, null, null, scrub: false, "CmdrJones", null, "v", DateTime.UtcNow);
        Assert.Contains("CmdrJones", r.Text);
        Assert.False(r.Scrubbed);
    }

    [Fact]
    public void ScrubOn_RemovesItFromTheBuiltFile()
    {
        var lines = new[] { Line("12:00:00", "Handle[CmdrJones]") };
        var r = GameLogExport.Build(lines, null, null, scrub: true, "CmdrJones", null, "v", DateTime.UtcNow);
        Assert.DoesNotContain("CmdrJones", r.Text);
        Assert.True(r.Scrubbed);
    }

    // ---- header -------------------------------------------------------------------------------

    // The header must SAY when personal details were kept. A user who turned scrubbing off and
    // forgot has to be able to see that from the file itself before they attach it to a public issue.
    [Fact]
    public void Header_StatesWhetherPersonalDetailsWereKept()
    {
        var kept = GameLogExport.Build(Sample, null, null, false, null, null, "v", DateTime.UtcNow).Text;
        var removed = GameLogExport.Build(Sample, null, null, true, null, null, "v", DateTime.UtcNow).Text;
        Assert.Contains("Personal details: KEPT", kept);
        Assert.Contains("identifies you", kept);
        Assert.Contains("Personal details: REMOVED", removed);
    }

    [Fact]
    public void Header_NamesTheRangeAndSaysItIsUtc()
    {
        var text = GameLogExport.Build(Sample, Utc("11:00:00"), Utc("13:00:00"), true, null, null, "v", DateTime.UtcNow).Text;
        Assert.Contains("2026-08-10 11:00:00", text);
        Assert.Contains("2026-08-10 13:00:00", text);
        Assert.Contains("UTC", text);
    }

    [Fact]
    public void Header_CountsWhatItKept()
        => Assert.Contains("Lines: 1 of 3",
            GameLogExport.Build(Sample, Utc("11:00:00"), Utc("13:00:00"), true, null, null, "v", DateTime.UtcNow).Text);

    [Fact]
    public void SuggestedFileName_SaysWhenItIsScrubbed()
    {
        var when = new DateTime(2026, 8, 10, 9, 5, 0);
        Assert.Equal("game_log_20260810_090500_scrubbed.txt", GameLogExport.SuggestedFileName(when, true));
        Assert.Equal("game_log_20260810_090500.txt", GameLogExport.SuggestedFileName(when, false));
    }

    // ---- source pins --------------------------------------------------------------------------

    // The dialog takes LOCAL times (a wall clock) and Game.log records UTC, so the conversion has to
    // happen or every exported range is silently wrong by the user's offset.
    [Fact]
    public void Dialog_ConvertsTheUsersLocalRangeToUtc()
    {
        var src = SourceFiles.ReadAppSource(@"Views\GameLogExportDialog.cs");
        Assert.Contains("fromLocal?.ToUniversalTime()", src);
        Assert.Contains("toLocal?.ToUniversalTime()", src);
    }

    // The game holds Game.log open for writing; a plain read throws exactly when a user is live and
    // trying to report a bug.
    [Fact]
    public void Dialog_ReadsTheLogWithSharedAccess()
        => Assert.Contains("FileShare.ReadWrite", SourceFiles.ReadAppSource(@"Views\GameLogExportDialog.cs"));

    [Fact]
    public void Settings_OffersTheExportFromTheDiagnosticsPane()
    {
        var src = SourceFiles.ReadAppSource(@"Views\SettingsPage.cs");
        Assert.Contains("Export Game.log", src);
        Assert.Contains("new GameLogExportDialog", src);
    }
}
