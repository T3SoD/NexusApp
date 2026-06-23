using System.IO;
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// Reads the user's own localization file (any mod, any custom format) and joins it to the bundled
// key -> official-name reference, producing customDisplay -> official. The join is the internal key,
// so the custom format never matters — verified here with real default (global1) and no-separator
// custom (global2) value shapes for the same keys.
public class GlobalIniReaderTests
{
    // Bundled reference, built from default-format lines exactly as the embedded list would be.
    private static System.Collections.Generic.IReadOnlyDictionary<string, string> Reference() =>
        ComponentStringReference.BuildMap(new[]
        {
            "item_Name_SHLD_GODI_S02_FR76=Mil/2/A FR-76",
            "item_NameCOOL_AEGS_S01_Tundra=Mil/1/D Tundra",
            "item_NameJUMP_TARS_S1_C=Civ/1/C Explorer",
        });

    [Fact]
    public void BuildCustomToOfficial_DefaultFormat_MapsToOfficial()
    {
        var map = GlobalIniReader.BuildCustomToOfficial(new[]
        {
            "item_Name_SHLD_GODI_S02_FR76=Mil/2/A FR-76",
            "item_NameCOOL_AEGS_S01_Tundra=Mil/1/D Tundra",
        }, Reference());

        Assert.Equal("FR-76", map["Mil/2/A FR-76"]);
        Assert.Equal("Tundra", map["Mil/1/D Tundra"]);
    }

    [Fact]
    public void BuildCustomToOfficial_NoSeparatorCustomFormat_MapsToOfficial()
    {
        // global2-style values: "2AFR-76", "1DTundra" — no separators at all, so only the key join works.
        var map = GlobalIniReader.BuildCustomToOfficial(new[]
        {
            "item_Name_SHLD_GODI_S02_FR76=2AFR-76",
            "item_NameCOOL_AEGS_S01_Tundra=1DTundra",
        }, Reference());

        Assert.Equal("FR-76", map["2AFR-76"]);
        Assert.Equal("Tundra", map["1DTundra"]);
    }

    [Fact]
    public void BuildCustomToOfficial_QuantumDriveBareName_MapsToOfficial()
    {
        var map = GlobalIniReader.BuildCustomToOfficial(new[]
        {
            "item_NameJUMP_TARS_S1_C=Explorer",
        }, Reference());

        Assert.Equal("Explorer", map["Explorer"]);
    }

    [Fact]
    public void BuildCustomToOfficial_SkipsDescAndNonItemLines()
    {
        var map = GlobalIniReader.BuildCustomToOfficial(new[]
        {
            "item_Desc_SHLD_GODI_S02_FR76=Item Type: Shield Generator. The FR-76 is great.",
            "2019_Ann_Sale_Day1=Expo-hall Day 01",
            "item_Name_SHLD_GODI_S02_FR76=2AFR-76",
        }, Reference());

        Assert.Single(map);
        Assert.Equal("FR-76", map["2AFR-76"]);
    }

    [Fact]
    public void BuildCustomToOfficial_HandlesBomOnFirstLine()
    {
        var map = GlobalIniReader.BuildCustomToOfficial(new[]
        {
            "\uFEFFitem_Name_SHLD_GODI_S02_FR76=2AFR-76",
        }, Reference());

        Assert.Equal("FR-76", map["2AFR-76"]);
    }

    [Fact]
    public void BuildCustomToOfficial_UnknownKey_IsIgnored()
    {
        var map = GlobalIniReader.BuildCustomToOfficial(new[]
        {
            "item_NameWEAP_BEHR_S99_NotInReference=Some Custom Gun",
        }, Reference());

        Assert.Empty(map);
    }

    [Fact]
    public void DeriveGlobalIniPath_PlacesItUnderLiveData()
    {
        var live = Path.Combine("X:", "StarCitizen", "LIVE");
        var expected = Path.Combine(live, "Data", "Localization", "english", "global.ini");

        Assert.Equal(expected, GlobalIniReader.DeriveGlobalIniPath(Path.Combine(live, "Game.log")));
    }

    [Fact]
    public void DeriveGlobalIniPath_EmptyInput_ReturnsNull()
    {
        Assert.Null(GlobalIniReader.DeriveGlobalIniPath(""));
        Assert.Null(GlobalIniReader.DeriveGlobalIniPath("   "));
    }

    [Fact]
    public void TryBuildFromFile_MissingFile_ReturnsNullWithoutThrowing()
    {
        var missing = Path.Combine(Path.GetTempPath(), "nexus_no_such_global_ini_zzz.ini");
        Assert.Null(GlobalIniReader.TryBuildFromFile(missing, Reference()));
    }
}
