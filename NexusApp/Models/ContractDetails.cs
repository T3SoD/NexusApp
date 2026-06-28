namespace NexusApp.Models;

// One Deliver/Collect leg of a contract, read from the in-game Contracts panel (OCR).
public sealed class ContractObjective
{
    public string Commodity { get; init; } = "";
    public int Scu { get; init; }
    public string Pickup { get; init; } = "";    // "Collect X from <pickup>"
    public string Dropoff { get; init; } = "";   // "Deliver N SCU of X to <dropoff>"
}

// A hauling contract's full detail, parsed from the Contracts panel. No player identity.
public sealed class ContractDetails
{
    public string Title { get; init; } = "";
    public int Reward { get; init; }             // aUEC
    public string ContractedBy { get; init; } = "";
    public List<ContractObjective> Objectives { get; init; } = new();
}
