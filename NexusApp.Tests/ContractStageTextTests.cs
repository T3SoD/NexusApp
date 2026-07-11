using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

public class ContractStageTextTests
{
    [Theory]
    [InlineData(null, "last scan: waiting")]
    [InlineData("parsed", "last scan: contract parsed")]
    [InlineData("notext", "last scan: no text found")]
    [InlineData("noregion", "last scan: no region set")]
    [InlineData("unavail", "last scan: scanner unavailable")]
    [InlineData("noanchor", "last scan: no contract found")]
    public void MapsEveryStage(string? stage, string expected) =>
        Assert.Equal(expected, ContractStageText.For(stage));

    [Fact]
    public void UnknownStage_FallsBackToWaiting() =>
        Assert.Equal("last scan: waiting", ContractStageText.For("garbage"));
}
