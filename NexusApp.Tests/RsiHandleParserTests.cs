using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// Real-format Game.log login lines (handle anonymized to "TestPilot") — confirms handle extraction
// matches the actual tokens and never picks up the accountId/geid on the same line.
public class RsiHandleParserTests
{
    private const string Primary =
        "<2026-06-22T00:01:30.292Z> [Notice] <Legacy login response> [CIG-net] User Login Success - Handle[TestPilot] - Time[187546778] [Team_GameServices][Login]";
    private const string Character =
        "<2026-06-22T00:01:29.680Z> [Notice] <AccountLoginCharacterStatus_Character> Character: createdAt 1778735676656 - updatedAt 1779731281967 - geid 204767770981 - accountId 5502541 - name TestPilot - state STATE_CURRENT [Team_GameServices][Login]";
    private const string Nickname =
        "<2026-06-22T00:01:33.576Z> [Notice] <Expect Incoming Connection> session=cd6 node_id=00 nickname=\"TestPilot\" playerGEID=204767770981 [Team_Network][Network][Gateway]";

    [Fact]
    public void Primary_ExtractsHandleFromLoginResponse()
    {
        Assert.True(RsiHandleParser.TryExtract(Primary, out var h));
        Assert.Equal("TestPilot", h);
    }

    [Fact]
    public void Fallback_ExtractsNameNotAccountIdOrGeid()
    {
        Assert.True(RsiHandleParser.TryExtract(Character, out var h));
        Assert.Equal("TestPilot", h);
    }

    [Fact]
    public void NicknameLine_IsNotUsed()
    {
        // We only trust the two authoritative login lines, not the nickname= network chatter.
        Assert.False(RsiHandleParser.TryExtract(Nickname, out _));
    }

    [Fact]
    public void UnrelatedLine_DoesNotMatch()
    {
        Assert.False(RsiHandleParser.TryExtract("<...> [Notice] <Some Event> nothing to see here", out _));
    }

    [Fact]
    public void ScanForLatest_ReturnsTheMostRecentLogin()
    {
        var lines = new[]
        {
            Primary.Replace("TestPilot", "OldHandle"),
            "noise line",
            Primary.Replace("TestPilot", "NewHandle"),
        };
        Assert.Equal("NewHandle", RsiHandleParser.ScanForLatest(lines));
    }

    [Fact]
    public void ScanForLatest_ReturnsNull_WhenNoLoginLines()
    {
        Assert.Null(RsiHandleParser.ScanForLatest(new[] { "a", "b", Nickname }));
    }

    [Fact]
    public void ScanFile_ReadsHandle_WithSharedAccess()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nexus_glog_{Guid.NewGuid():N}.log");
        File.WriteAllText(path, $"{Character}\n{Primary}\nsome other line\n");
        try { Assert.Equal("TestPilot", RsiHandleParser.ScanFile(path)); }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void ScanFile_MissingFile_ReturnsNull()
    {
        Assert.Null(RsiHandleParser.ScanFile(Path.Combine(Path.GetTempPath(), $"missing_{Guid.NewGuid():N}.log")));
    }
}
