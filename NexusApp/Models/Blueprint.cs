namespace NexusApp.Models;

public class Blueprint
{
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";  // Armor Weapons Ammo Ship Components
    public string? SubCategory { get; set; }    // Cooler Power Plant Quantum Drive Radar Refueling Shield Mining Laser Salvage Ship Weapon Tractor Beam
    public List<BlueprintIngredient> Ingredients { get; set; } = [];
    public List<BlueprintUnlockEntry> UnlockEntries { get; set; } = [];
    public List<BlueprintSlot> Slots { get; set; } = [];
    public int? CraftSeconds { get; set; }
}

public record BlueprintIngredient(string ResourceName, double Quantity, string Unit);

public record BlueprintUnlockEntry(
    string Faction,
    string? MissionType,
    string MissionTitle,
    string? Rank,
    string[]? Systems
);

// Crafting quality forecast models. A range is either multiplicative (M0/M1) or additive (A0/A1),
// never both, so IsAdditive keys off A0 alone.
public record QualityRange(int Q0, int Q1, double? M0, double? M1, int? A0, int? A1)
{
    public bool IsAdditive => A0.HasValue;
}

public record StatModifier(string Stat, List<QualityRange> Ranges);

public record BlueprintSlot(string Name, List<string> Resources, double Quantity, string Unit, bool IsItem, List<StatModifier> Modifiers);

public record QualityBand(int Start, int End, int Mapped);

public record CraftStat(string Key, string Name, Dictionary<string, string>? Override);
