namespace NexusApp.Models;

public enum HaulRole { Pickup, Dropoff }

// Mirrors EndMission CompletionType values, plus Active while the mission is open.
public enum HaulOutcome { Active, Complete, Abandoned, Failed, Deactivated }

// One pickup or dropoff objective of a haul. Pickup and dropoff that move the same
// cargo share CargoKey (the objective GUID base + leg index), so a pickup can borrow
// its sibling dropoff's commodity / SCU for display.
public sealed class HaulLeg
{
    public string ObjectiveId { get; init; } = "";   // e.g. "dropoff_6e9335e8-..._0"
    public string CargoKey { get; init; } = "";       // "<guidBase>_<leg>" shared by the pickup/dropoff pair
    public HaulRole Role { get; init; }
    public int LegIndex { get; init; }
    public string Commodity { get; set; } = "";       // dropoff only (from the Deliver line)
    public int TargetScu { get; set; }                // dropoff only
    public string Destination { get; set; } = "";     // dropoff only (the "to <X>")
    public double X { get; init; }
    public double Y { get; init; }
    public double Z { get; init; }
    public bool Completed { get; set; }
}

public sealed class Haul
{
    public string MissionId { get; init; } = "";
    public string Company { get; set; } = "";         // display name, e.g. "Red Wind"
    public string ContractName { get; set; } = "";    // raw contract token
    public string Topology { get; set; } = "Unknown"; // "1 to 1" / "1 to 2" / "1 to 3" / "2 to 1" / "Unknown"
    public string RouteTitle { get; set; } = "";      // Contract Accepted title, EM tags stripped
    public string PickupName { get; set; } = "";      // best-effort, left side of an "A > B" route title
    public int Reward { get; set; }                 // aUEC, from OCR (0 = unknown)
    public string ContractedBy { get; set; } = "";  // from OCR (cleaner than the generator)
    public List<ContractObjective> ContractObjectives { get; set; } = new();  // OCR-sourced
    public int? ContainerCap { get; set; }           // max container SCU from contract OCR (null = unknown)
    public HaulOutcome Outcome { get; set; } = HaulOutcome.Active;
    public DateTime StartedAt { get; init; } = DateTime.Now;
    public List<HaulLeg> Legs { get; } = new();

    public bool IsActive => Outcome == HaulOutcome.Active;
    public HaulLeg? LegByObjective(string objectiveId) =>
        Legs.Find(l => l.ObjectiveId == objectiveId);
}

// A single place to act on, with everything to load or drop there across all active hauls.
public sealed class ConsolidationStop
{
    public string Location { get; init; } = "";
    public List<(string Commodity, int Scu, string MissionId)> Items { get; } = new();
    public int TotalScu => Items.Sum(i => i.Scu);
}

// The two groupings the overlay/main window render: where to load, where to drop.
public sealed class Consolidation
{
    public List<ConsolidationStop> Pickups { get; } = new();
    public List<ConsolidationStop> Dropoffs { get; } = new();
}
