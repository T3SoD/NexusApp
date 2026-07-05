using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// The bundled reference maps each internal component key to its clean official name, derived
// from the default-format component string list by stripping the "Class/Size/Grade " prefix.
// The key is the mod-independent anchor, so it must normalize the two key spellings the game
// uses (item_Name… and item_Name_…) to the same form.
public class ComponentStringReferenceTests
{
    [Fact]
    public void BuildMap_StripsDefaultPrefix_ToOfficialName()
    {
        var map = ComponentStringReference.BuildMap(new[]
        {
            "item_Name_SHLD_GODI_S02_FR76=Mil/2/A FR-76",
            "item_NameCOOL_AEGS_S01_Tundra=Mil/1/D Tundra",
        });

        Assert.Equal("FR-76", map[ComponentStringReference.NormalizeKey("item_Name_SHLD_GODI_S02_FR76")]);
        Assert.Equal("Tundra", map[ComponentStringReference.NormalizeKey("item_NameCOOL_AEGS_S01_Tundra")]);
    }

    [Fact]
    public void BuildMap_KeepsMultiWordName_AfterPrefix()
    {
        var map = ComponentStringReference.BuildMap(new[]
        {
            "item_NameCOOL_JSPN_S00_FrostStarSL=Civ/0/C Frost-Star SL",
        });

        Assert.Equal("Frost-Star SL", map[ComponentStringReference.NormalizeKey("item_NameCOOL_JSPN_S00_FrostStarSL")]);
    }

    [Fact]
    public void BuildMap_QuantumDrive_NameOnlyInValue()
    {
        // The key (JUMP_TARS_S1_C) does not contain the model name - it comes from the value.
        var map = ComponentStringReference.BuildMap(new[] { "item_NameJUMP_TARS_S1_C=Civ/1/C Explorer" });
        Assert.Equal("Explorer", map[ComponentStringReference.NormalizeKey("item_NameJUMP_TARS_S1_C")]);
    }

    [Fact]
    public void BuildMap_NormalizesBothKeySpellings_ToSameEntry()
    {
        var map = ComponentStringReference.BuildMap(new[]
        {
            "item_NameSHLD_GODI_S01_FR66=Mil/1/A FR-66",
            "item_Name_SHLD_GODI_S01_FR66=Mil/1/A FR-66",
        });

        Assert.Single(map);
        Assert.Equal("FR-66", map[ComponentStringReference.NormalizeKey("item_Name_SHLD_GODI_S01_FR66")]);
    }

    [Fact]
    public void BuildMap_SkipsBomBlankAndNonEntryLines()
    {
        var map = ComponentStringReference.BuildMap(new[]
        {
            "﻿item_NameQDRV_TARS_S03_Ranger=Civ/3/B Ranger",   // BOM on first line
            "",
            "   ",
            "this line has no equals sign",
        });

        Assert.Single(map);
        Assert.Equal("Ranger", map[ComponentStringReference.NormalizeKey("item_NameQDRV_TARS_S03_Ranger")]);
    }

    [Fact]
    public void NormalizeKey_StripsOptionalUnderscoreAfterItemName()
    {
        Assert.Equal(
            ComponentStringReference.NormalizeKey("item_NameSHLD_GODI_S02_FR76"),
            ComponentStringReference.NormalizeKey("item_Name_SHLD_GODI_S02_FR76"));
    }

    [Fact]
    public void NormalizeKey_LeavesPlainKeyUnchanged()
    {
        Assert.Equal("item_NameCOOL_AEGS_S01_Tundra",
            ComponentStringReference.NormalizeKey("item_NameCOOL_AEGS_S01_Tundra"));
    }
}
