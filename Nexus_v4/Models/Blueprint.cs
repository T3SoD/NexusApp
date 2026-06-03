namespace Nexus_v4.Models;

public class Blueprint
{
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";  // Armor Weapons Ammo Ship Components
    public string? SubCategory { get; set; }    // Cooler Power Plant Quantum Drive Radar Refueling Shield Mining Laser Salvage Ship Weapon Tractor Beam
    public List<BlueprintIngredient> Ingredients { get; set; } = [];
    public List<BlueprintUnlockEntry> UnlockEntries { get; set; } = [];
}

public record BlueprintIngredient(string ResourceName, double Quantity, string Unit);

public record BlueprintUnlockEntry(
    string Faction,
    string? MissionType,
    string MissionTitle,
    string? Rank,
    string[]? Systems
);
