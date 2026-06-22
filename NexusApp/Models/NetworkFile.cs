namespace NexusApp.Models;

/// <summary>
/// The on-disk shape of a .nexuslib file. "library" carries a single <see cref="Member"/> (one
/// person sharing their own library); "roster" carries <see cref="Members"/> (a coordinator's
/// combined export of many people).
/// </summary>
public sealed class NetworkFile
{
    public int Schema { get; set; }
    public string Kind { get; set; } = "";
    public string? ExportedAtUtc { get; set; }
    public string? App { get; set; }
    public string? GroupName { get; set; }                  // roster only
    public NetworkFileMember? Member { get; set; }          // library only
    public List<NetworkFileMember>? Members { get; set; }   // roster only
}

public sealed class NetworkFileMember
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string IdentityKind { get; set; } = NetworkIdentityKind.Handle;
    public string? RsiHandle { get; set; }
    public string? UpdatedAtUtc { get; set; }
    public List<string> OwnedBlueprints { get; set; } = new();
}

public static class NetworkFileKind
{
    public const string Library = "library";
    public const string Roster = "roster";
}
