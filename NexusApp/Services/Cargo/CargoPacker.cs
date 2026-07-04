using NexusApp.Models.Cargo;

namespace NexusApp.Services.Cargo;

// One box queued for placement: a standard SCU size, a back-link to its manifest line, and a
// stable order index so the packing order is a strict total order (fully deterministic).
public sealed class PackBox
{
    public int Scu { get; init; }
    public string? ItemId { get; init; }
    public int OrderIndex { get; init; }
    public BoxType Type => BoxType.Of(Scu);
}

// The outcome of one auto-pack: what landed, what did not, and the live occupancy grids.
public sealed class PackResult
{
    public List<Placement> Placed { get; } = new();
    public List<PackBox> Deferred { get; } = new();
    public IReadOnlyList<GridOccupancy> Grids { get; init; } = Array.Empty<GridOccupancy>();

    public int PlacedScu => Placed.Sum(p => p.Scu);
    public int DeferredScu => Deferred.Sum(b => b.Scu);
}

// Deterministic 3D bin packing: greedy largest-first SCU split, then support-constrained
// bottom-back-left first-fit across a ship's grids. A pure function of (grids, boxes), so the
// same input yields a byte-identical layout on every run and machine.
public static class CargoPacker
{
    // Split a total SCU into physical boxes, greedy largest-first, capped. 1 SCU divides every
    // total, so the remainder is always zero and the box SCU always sums back to the input.
    public static List<int> SplitToBoxes(int totalScu, int cap)
    {
        var boxes = new List<int>();
        int remaining = Math.Max(0, totalScu);
        foreach (var size in BoxType.SizesDesc)
        {
            if (size > cap) continue;
            while (remaining >= size)
            {
                boxes.Add(size);
                remaining -= size;
            }
        }
        return boxes;
    }

    // Turn manifest lines into a flat box queue, splitting each under its effective cap.
    public static List<PackBox> BuildBoxes(IReadOnlyList<ManifestItem> items, int shipMaxCap)
    {
        var boxes = new List<PackBox>();
        int idx = 0;
        foreach (var item in items)
        {
            int cap = item.EffectiveCap(shipMaxCap);
            foreach (var scu in SplitToBoxes(item.Scu, cap))
                boxes.Add(new PackBox { Scu = scu, ItemId = item.Id, OrderIndex = idx++ });
        }
        return boxes;
    }

    // Strict total order: SCU (= volume) descending, then the stable input index. No ties reach
    // placement, so there is never an arbitrary choice to resolve.
    private static int CompareBoxes(PackBox a, PackBox b)
    {
        int c = b.Scu.CompareTo(a.Scu);
        return c != 0 ? c : a.OrderIndex.CompareTo(b.OrderIndex);
    }

    // Strict grid order: capacity descending, then max container descending, then id ascending.
    private static int CompareGrids(GridOccupancy a, GridOccupancy b)
    {
        int c = b.Grid.Capacity.CompareTo(a.Grid.Capacity);
        if (c != 0) return c;
        c = b.Grid.MaxContainerScu.CompareTo(a.Grid.MaxContainerScu);
        return c != 0 ? c : a.Grid.Id.CompareTo(b.Grid.Id);
    }

    // Bottom-back-left scan: z ascending (gravity, lowest first), then y, then x; try each
    // orientation in fixed order; take the first anchor that fits and is supported. Marks on success.
    public static Placement? TryPlaceInGrid(GridOccupancy g, PackBox box, bool requireSupport = true)
    {
        for (int z = 0; z < g.Grid.H; z++)
            for (int y = 0; y < g.Grid.D; y++)
                for (int x = 0; x < g.Grid.W; x++)
                    foreach (var size in box.Type.Orientations)
                        if (g.Fits(x, y, z, size) && (!requireSupport || g.IsSupported(x, y, z, size)))
                        {
                            g.Mark(x, y, z, size, true);
                            return new Placement
                            {
                                GridId = g.Grid.Id, Scu = box.Scu, ItemId = box.ItemId,
                                X = x, Y = y, Z = z, Size = size,
                            };
                        }
        return null;
    }

    private static Placement? TryPlaceInAnyGrid(IReadOnlyList<GridOccupancy> grids, PackBox box, bool requireSupport)
    {
        foreach (var g in grids)
        {
            if (box.Scu > g.Grid.MaxContainerScu) continue;   // respect each grid's cap independently
            var p = TryPlaceInGrid(g, box, requireSupport);
            if (p != null) return p;
        }
        return null;
    }

    public static PackResult AutoPack(IReadOnlyList<GridDef> grids, IReadOnlyList<PackBox> boxes, bool requireSupport = true)
    {
        var occ = grids.Select(g => new GridOccupancy(g)).ToList();
        occ.Sort(CompareGrids);

        var queue = boxes.OrderBy(b => b, Comparer<PackBox>.Create(CompareBoxes)).ToList();
        var result = new PackResult { Grids = occ };

        foreach (var box in queue)
        {
            var p = TryPlaceInAnyGrid(occ, box, requireSupport);
            if (p != null) result.Placed.Add(p);
            else result.Deferred.Add(box);
        }

        GapFillSmall(occ, result, requireSupport);
        return result;
    }

    // Retry the small (1/2/4 SCU) deferred boxes against the final occupancy: they backfill pockets
    // the large-first pass left behind. Deterministic (deferred list is already in sorted order).
    private static void GapFillSmall(IReadOnlyList<GridOccupancy> occ, PackResult result, bool requireSupport)
    {
        if (result.Deferred.Count == 0) return;
        var still = new List<PackBox>();
        foreach (var box in result.Deferred)
        {
            if (box.Scu > 4) { still.Add(box); continue; }
            var p = TryPlaceInAnyGrid(occ, box, requireSupport);
            if (p != null) result.Placed.Add(p);
            else still.Add(box);
        }
        result.Deferred.Clear();
        result.Deferred.AddRange(still);
    }
}
