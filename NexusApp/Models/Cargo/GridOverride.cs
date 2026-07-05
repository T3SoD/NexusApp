namespace NexusApp.Models.Cargo;

// One editable cargo grid in ship-space cells, the volume CENTER at Px/Py/Pz. Mirrors the
// catalog's raw grid shape so hand corrections made in the planner's Edit Layout mode round-trip
// losslessly through the override store and the datamine converter. W = lateral, D = fore-aft,
// H = vertical (gravity); Cap = max container SCU (standard sizes only); Wy = the W axis runs
// fore-aft rather than lateral.
public sealed record GridOverride
{
    public int Id { get; init; }
    public int W { get; init; }
    public int D { get; init; }
    public int H { get; init; }
    public int Cap { get; init; }                 // largest accepted; kept for back-compat
    public List<int>? Accepts { get; init; }      // exact accepted-size set (null = derive from Cap)
    // Ship-space grid CENTER, cells. Null means "no datamined position" (schematic ship); it must
    // round-trip as null through export/import so an unpositioned ship is not silently pinned to
    // (0,0,0) and stacked at the origin.
    public double? Px { get; init; }
    public double? Py { get; init; }
    public double? Pz { get; init; }
    public bool Wy { get; init; }
    // Optional orientation quaternion (x, y, z, w) in the renderer's box-local frame, for genuinely
    // tilted grids. Null = axis-aligned. Carried losslessly through export/import and the edit
    // round-trip so a save never silently un-tilts a datamined grid; the editor does not change it.
    public List<double>? Rot { get; init; }
}
