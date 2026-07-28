using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// Blueprint Library grouping: skin/colour variants of the same model collapse into one family
// row. Armor pieces are recognized by a fixed 7-word list (falling back to "Other"); every other
// category groups by stripping quoted skin names, parenthetical decorations, and trailing
// colour/edition words down to a bare family key.
public class BlueprintFamilyGroupingTests
{
    [Theory]
    [InlineData("Ractis Helmet", "Helmet")]
    [InlineData("Ractis Core", "Core")]
    [InlineData("Ractis Arms", "Arms")]
    [InlineData("Ractis Legs", "Legs")]
    [InlineData("Ractis Backpack", "Backpack")]
    [InlineData("Ractis Undersuit", "Undersuit")]
    [InlineData("Ractis Suit", "Suit")]
    public void ArmorPiece_MatchesEachOfTheSevenPieceWords(string name, string expectedPiece)
    {
        Assert.Equal(expectedPiece, BlueprintFamilyGrouping.ArmorPiece(name));
    }

    [Fact]
    public void ArmorPiece_NoMatch_FallsBackToOther()
    {
        Assert.Equal("Other", BlueprintFamilyGrouping.ArmorPiece("Ractis Gauntlet"));
    }

    [Fact]
    public void StripDecorations_RemovesQuotedSkinName()
    {
        Assert.Equal("Aegis Avenger Titan", BlueprintFamilyGrouping.StripDecorations("Aegis Avenger Titan \"Nightrunner\""));
    }

    [Fact]
    public void StripDecorations_RemovesParentheticalDecoration()
    {
        Assert.Equal("Aegis Avenger Titan", BlueprintFamilyGrouping.StripDecorations("Aegis Avenger Titan (Best in Show)"));
    }

    [Fact]
    public void FamilyKey_ColourWordSuffix_CollapsesToSameFamilyAsUndecoratedName()
    {
        var undecorated = BlueprintFamilyGrouping.FamilyKey("Aegis Avenger Titan");
        var colourSuffixed = BlueprintFamilyGrouping.FamilyKey("Aegis Avenger Titan Storm");
        Assert.Equal(undecorated, colourSuffixed);
        Assert.Equal("Aegis Avenger Titan", colourSuffixed);
    }
}
