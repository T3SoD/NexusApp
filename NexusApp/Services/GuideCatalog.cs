namespace NexusApp.Services;

// One curated mission guide image. PackUri points at an embedded Resource under Assets\Guides.
// NativeWidth/NativeHeight are the real pixel dimensions, used for fit math and the card dims line
// without decoding the bitmap.
public sealed record GuideEntry(string Id, string Title, string Category, string PackUri, int NativeWidth, int NativeHeight);

// Single source of truth for the Mission Guides feature (main page and overlay tab both read this).
// Adding a guide: drop the PNG under Assets\Guides\<Category>\ and add one entry here
// (GuideCatalogTests enforces both directions, so a missing entry or a missing file fails the suite).
public static class GuideCatalog
{
    public static IReadOnlyList<GuideEntry> All { get; } =
    [
        new("checkmate",   "Checkmate",           "Contested Zones",        "pack://application:,,,/Assets/Guides/ContestedZones/CheckmateCZMap.png",             5500, 3593),
        new("orbituary",   "Orbituary",           "Contested Zones",        "pack://application:,,,/Assets/Guides/ContestedZones/OrbituaryCZMap.png",             5496, 5296),
        new("ruin",        "Ruin",                "Contested Zones",        "pack://application:,,,/Assets/Guides/ContestedZones/RuinCZMap.png",                  5600, 3594),
        new("tsg-overview","Overview",            "Tactical Strike Groups", "pack://application:,,,/Assets/Guides/TacticalStrikeGroups/TacticalStrikeGroups.png", 4961, 3508),
        new("tsg-trench",  "Trench Runner",       "Tactical Strike Groups", "pack://application:,,,/Assets/Guides/TacticalStrikeGroups/TSGTrenchRunner.png",      4961, 3508),
        new("asd-onyx",    "ASD Onyx Facilities", "General",                "pack://application:,,,/Assets/Guides/General/ASDOnyxFacilities.png",                 3841, 4321),
    ];

    // Distinct categories in catalog order, which is the display order on both surfaces.
    public static IReadOnlyList<string> Categories { get; } = All.Select(g => g.Category).Distinct().ToList();

    public static IEnumerable<GuideEntry> ByCategory(string category) => All.Where(g => g.Category == category);
}
