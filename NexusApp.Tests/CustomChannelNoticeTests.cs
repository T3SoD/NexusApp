using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

public class CustomChannelNoticeTests
{
    [Fact]
    public void Shows_ForUnauthorizedCustom_OnANewPath()
        => Assert.True(CustomChannelNotice.ShouldShow(GameChannel.Custom, @"X:\Odd\Game.log", "", authorized: false));

    [Fact]
    public void Hidden_WhenAlreadyDismissedForThisExactPath_CaseInsensitive()
        => Assert.False(CustomChannelNotice.ShouldShow(GameChannel.Custom, @"X:\Odd\Game.log", @"x:\ODD\game.log", authorized: false));

    [Fact]
    public void Shows_Again_ForADifferentCustomPath()
        => Assert.True(CustomChannelNotice.ShouldShow(GameChannel.Custom, @"X:\Odd2\Game.log", @"X:\Odd\Game.log", authorized: false));

    [Fact]
    public void Hidden_WhenAuthorized_OrOnKnownChannels()
    {
        Assert.False(CustomChannelNotice.ShouldShow(GameChannel.Custom, @"X:\Odd\Game.log", "", authorized: true));
        Assert.False(CustomChannelNotice.ShouldShow(GameChannel.Live, @"X:\SC\LIVE\Game.log", "", authorized: false));
        Assert.False(CustomChannelNotice.ShouldShow(GameChannel.Ptu, @"X:\SC\PTU\Game.log", "", authorized: false));
    }
}
