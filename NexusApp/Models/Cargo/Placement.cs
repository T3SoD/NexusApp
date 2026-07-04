namespace NexusApp.Models.Cargo;

// One placed container. The anchor cell is mutable so the drag interaction can move a box
// through the same occupancy primitives the packer used. Size is the oriented footprint,
// which may differ from BoxType.Size by a 90-degree yaw.
public sealed class Placement
{
    public int GridId { get; init; }
    public int Scu { get; init; }
    public string? ItemId { get; init; }     // back-link to the manifest line (color / label / fill)
    public int X { get; set; }
    public int Y { get; set; }
    public int Z { get; set; }               // vertical
    public CellSize Size { get; set; }       // oriented footprint
    public bool Valid { get; set; } = true;
}
