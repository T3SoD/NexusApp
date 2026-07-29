using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

public class GameChannelsTests
{
    [Theory]
    [InlineData(@"C:\Program Files\Roberts Space Industries\StarCitizen\LIVE\Game.log", GameChannel.Live)]
    [InlineData(@"C:\Program Files\Roberts Space Industries\StarCitizen\HOTFIX\Game.log", GameChannel.Hotfix)]
    [InlineData(@"D:\Games\StarCitizen\PTU\Game.log", GameChannel.Ptu)]
    [InlineData(@"D:\Games\StarCitizen\EPTU\Game.log", GameChannel.Eptu)]
    [InlineData(@"D:\Games\StarCitizen\TECH-PREVIEW\Game.log", GameChannel.TechPreview)]
    [InlineData(@"D:\Games\StarCitizen\live\Game.log", GameChannel.Live)]   // case-insensitive
    [InlineData(@"D:\Games\MyWeirdFolder\Game.log", GameChannel.Custom)]
    [InlineData(@"Game.log", GameChannel.Custom)]                            // no parent folder
    [InlineData("", GameChannel.Custom)]
    [InlineData(null, GameChannel.Custom)]
    public void FromLogPath_MapsParentFolder(string? path, GameChannel expected)
        => Assert.Equal(expected, GameChannels.FromLogPath(path));

    [Theory]
    [InlineData(GameChannel.Live, false, true)]
    [InlineData(GameChannel.Hotfix, false, true)]     // HOTFIX = LIVE backend, real progress
    [InlineData(GameChannel.Ptu, true, false)]        // test channels never record, even "authorized"
    [InlineData(GameChannel.Eptu, true, false)]
    [InlineData(GameChannel.TechPreview, true, false)]
    [InlineData(GameChannel.Custom, false, false)]    // custom records only with the checkbox
    [InlineData(GameChannel.Custom, true, true)]
    public void RecordsRealData_Matrix(GameChannel c, bool customAuthorized, bool expected)
        => Assert.Equal(expected, GameChannels.RecordsRealData(c, customAuthorized));

    [Fact]
    public void KnownFolders_ContainsHotfix_LiveFirst()
    {
        Assert.Equal("LIVE", GameChannels.KnownFolders[0]);   // probe order: LIVE stays the default hit
        Assert.Contains("HOTFIX", GameChannels.KnownFolders); // issue #28: was missing entirely
    }

    [Fact]
    public void ChipSuffix_EmptyOnLiveOnly()
    {
        Assert.Equal("", GameChannels.ChipSuffix(GameChannel.Live));
        Assert.Equal(" \u00b7 HOTFIX", GameChannels.ChipSuffix(GameChannel.Hotfix));
        Assert.Equal(" \u00b7 CUSTOM", GameChannels.ChipSuffix(GameChannel.Custom));
    }

    [Fact]
    public void IsTest_TrueForWipedEnvironmentsOnly()
    {
        Assert.True(GameChannels.IsTest(GameChannel.Ptu));
        Assert.True(GameChannels.IsTest(GameChannel.Eptu));
        Assert.True(GameChannels.IsTest(GameChannel.TechPreview));
        Assert.False(GameChannels.IsTest(GameChannel.Live));
        Assert.False(GameChannels.IsTest(GameChannel.Hotfix));
        Assert.False(GameChannels.IsTest(GameChannel.Custom));
    }
}
