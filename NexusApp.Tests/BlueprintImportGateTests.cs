using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

public class BlueprintImportGateTests
{
    [Theory]
    [InlineData(GameChannel.Live, false)]
    [InlineData(GameChannel.Hotfix, false)]
    [InlineData(GameChannel.Custom, true)]   // authorized custom imports fine
    public void Refusal_Null_WhenRecordingAllowed(GameChannel c, bool auth)
        => Assert.Null(BlueprintImportGate.Refusal(c, auth));

    [Theory]
    [InlineData(GameChannel.Ptu)]
    [InlineData(GameChannel.Eptu)]
    [InlineData(GameChannel.TechPreview)]
    public void Refusal_TestChannels_NamesTheChannel(GameChannel c)
    {
        var msg = BlueprintImportGate.Refusal(c, customAuthorized: true);
        Assert.NotNull(msg);
        Assert.Contains(GameChannels.FolderName(c), msg);
    }

    [Fact]
    public void Refusal_UnauthorizedCustom_PointsAtTheSetting()
    {
        var msg = BlueprintImportGate.Refusal(GameChannel.Custom, customAuthorized: false);
        Assert.NotNull(msg);
        Assert.Contains("Settings", msg);
    }
}
