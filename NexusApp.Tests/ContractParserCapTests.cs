using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// The container-size cap lives in the contract details prose, separate from the objective lines.
public class ContractParserCapTests
{
    [Fact]
    public void ParseCap_ReadsContainersOrSmaller()
    {
        Assert.Equal(16, ContractParser.ParseContainerCap(
            "All cargo will be delivered in 16 SCU containers or smaller."));
    }

    [Fact]
    public void ParseCap_ReadsMaximumContainerSize()
    {
        Assert.Equal(32, ContractParser.ParseContainerCap(
            "Note: maximum container size of 32 SCU for this contract."));
    }

    [Fact]
    public void ParseCap_SnapsToNearestStandardSize()
    {
        // A misread or non-standard figure snaps to the closest real box size.
        Assert.Equal(16, ContractParser.ParseContainerCap("cargo in 18 SCU containers"));
    }

    [Fact]
    public void ParseCap_ToleratesOcrSlopOnScu()
    {
        Assert.Equal(8, ContractParser.ParseContainerCap("delivered in 8 SCIJ containers"));
    }

    [Fact]
    public void ParseCap_ReturnsNullWhenNoContainerPhrase()
    {
        Assert.Null(ContractParser.ParseContainerCap(
            "Deliver 0/148 SCU of Titanium to Everus Harbor."));
    }

    [Fact]
    public void Parse_AttachesContainerCapToDetails()
    {
        var text = "x 139,250 N/A Covalex Independent Contractors " +
                   "All cargo delivered in 24 SCU containers or smaller. " +
                   "Deliver 0/148 SCU of Titanium to Everus Harbor. TRACK";
        var d = ContractParser.Parse(text);
        Assert.NotNull(d);
        Assert.Equal(24, d!.ContainerCap);
    }
}
