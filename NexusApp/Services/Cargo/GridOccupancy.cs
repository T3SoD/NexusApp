using NexusApp.Models.Cargo;

namespace NexusApp.Services.Cargo;

// Mutable 3D cell occupancy for one cargo grid. These three primitives (Fits, IsSupported, Mark)
// are the single shared truth: the auto-packer and the drag validator both call them, so a
// hand-dragged layout can never break a rule the packer enforced. Integer-only, so deterministic.
public sealed class GridOccupancy
{
    public GridDef Grid { get; }
    private readonly bool[,,] _occ;   // [x, y, z]

    public GridOccupancy(GridDef grid)
    {
        Grid = grid;
        _occ = new bool[grid.W, grid.D, grid.H];
    }

    public void Clear() => Array.Clear(_occ, 0, _occ.Length);

    // True if the box fits in bounds and every cell it would occupy is free.
    public bool Fits(int x, int y, int z, CellSize size)
    {
        if (x < 0 || y < 0 || z < 0) return false;
        if (x + size.W > Grid.W || y + size.D > Grid.D || z + size.H > Grid.H) return false;
        for (int cx = x; cx < x + size.W; cx++)
            for (int cy = y; cy < y + size.D; cy++)
                for (int cz = z; cz < z + size.H; cz++)
                    if (_occ[cx, cy, cz]) return false;
        return true;
    }

    // Full-footprint support: resting on the floor (z == 0) or every cell directly beneath is filled.
    // Keeps stacks gravity-plausible so the 3D view reads like a real cargo hold.
    public bool IsSupported(int x, int y, int z, CellSize size)
    {
        if (z == 0) return true;
        for (int cx = x; cx < x + size.W; cx++)
            for (int cy = y; cy < y + size.D; cy++)
                if (!_occ[cx, cy, z - 1]) return false;
        return true;
    }

    public void Mark(int x, int y, int z, CellSize size, bool value)
    {
        for (int cx = x; cx < x + size.W; cx++)
            for (int cy = y; cy < y + size.D; cy++)
                for (int cz = z; cz < z + size.H; cz++)
                    _occ[cx, cy, cz] = value;
    }

    public int UsedCells()
    {
        int n = 0;
        foreach (var filled in _occ) if (filled) n++;
        return n;
    }
}
