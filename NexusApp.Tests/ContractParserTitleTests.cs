using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// ContractParser.NormalizeTitle/ExtractTitle (Services/ContractParser.cs:123-141) had zero direct
// test coverage. NormalizeTitle(d.Title) (ContractScanner.cs:77) is the sole key that decides
// whether a scanned contract is "new" and fires ContractScanned - a wrong normalization would
// silently merge two different contracts into one dedup bucket, or split one into two.
//
// Note (verifier correction on the source finding): the per-state [CONTRACT] log dedup is a
// SEPARATE mechanism keyed on NormalizeTitle(d.ContractedBy) at a different call site
// (ContractScanner.cs:108/126-127), not on the Title-based key tested here. These tests cover only
// the ContractScanned-firing key (Title normalization) and ExtractTitle's anchor selection.
public class ContractParserTitleTests
{
    // ---- NormalizeTitle: public, tested directly ----

    [Fact]
    public void NormalizeTitle_StripsRepTag()
    {
        Assert.Equal("need a hauler", ContractParser.NormalizeTitle("Need a Hauler [50/200 Rep]"));
    }

    [Fact]
    public void NormalizeTitle_FoldsCase()
    {
        Assert.Equal("need a hauler", ContractParser.NormalizeTitle("NEED A HAULER"));
    }

    [Fact]
    public void NormalizeTitle_CollapsesPunctuationToSpace()
    {
        Assert.Equal("deliver to anywhere", ContractParser.NormalizeTitle("Deliver-to: Anywhere!!"));
    }

    [Fact]
    public void NormalizeTitle_EmptyString_ReturnsEmpty()
    {
        Assert.Equal("", ContractParser.NormalizeTitle(""));
    }

    [Fact]
    public void NormalizeTitle_WhitespaceOnly_ReturnsEmpty()
    {
        Assert.Equal("", ContractParser.NormalizeTitle("   "));
    }

    // ---- ExtractTitle: private, so it is exercised the same way production code reaches it -
    // through Parse(...)'s Title field (Services/ContractParser.cs:74). ----

    [Fact]
    public void ExtractTitle_ViaParse_PicksHighestPriorityAnchor_EvenWhenALowerPriorityAnchorAppearsEarlier()
    {
        // "PRIMARY OBJECTIVES" appears earlier in the raw text than "DETAILS", but ExtractTitle's
        // anchor list checks "DETAILS" before "PRIMARY OBJECTIVES" (Services/ContractParser.cs:125),
        // so the title is cut at DETAILS's position - swallowing the earlier "PRIMARY OBJECTIVES Foo"
        // text into the returned title rather than stopping at it. This pins that priority-order
        // behavior, not just "an anchor is found".
        var d = ContractParser.Parse("PRIMARY OBJECTIVES Foo DETAILS Bar N/A Org TRACK");
        Assert.NotNull(d);
        Assert.Equal("PRIMARY OBJECTIVES Foo", d!.Title);
    }

    [Fact]
    public void ExtractTitle_ViaParse_NoAnchorFound_FallsBackToWholeCollapsedText()
    {
        // No occurrence of "DETAILS" / "PRIMARY OBJECTIVES" / "ACCEPT" / "TRACK" anywhere in the
        // text, so ExtractTitle's loop exhausts and hits its fallback: Collapse(whole text).
        const string text =
            "x 139,250 N/A Some Org Deliver 0/5 SCU of Iron to Place. o Collect Iron from Spot.";
        var d = ContractParser.Parse(text);
        Assert.NotNull(d);
        Assert.Equal(text, d!.Title);
    }
}
