namespace NexusApp.Services;

/// <summary>One line of a container plan: buy <see cref="Count"/> crates of <see cref="Scu"/> SCU.</summary>
public readonly record struct ContainerPick(int Scu, int Count);

/// <summary>
/// What to actually buy at a kiosk to reach the planner's recommended quantity.
/// <para>
/// <see cref="TotalScu"/> is what the picks really add up to, which is NOT always the target: a
/// kiosk selling only 16/24/32 SCU crates cannot make 20, and a Cutlass Black's 46 SCU cannot be
/// filled from 8 SCU crates without leaving 6 behind. <see cref="ShortfallScu"/> is that gap, and it
/// is the number the owner was hitting in the wild ("the planner recommends an amount of SCU for a
/// trip but when I get there it's limited to a container size that is not a multiple of what the
/// planner recommends").
/// </para>
/// </summary>
public sealed record ContainerPlan(int TotalScu, int ShortfallScu, IReadOnlyList<ContainerPick> Picks,
                                   int MinContainerScu, int MaxContainerScu)
{
    /// <summary>Crates to move. Drives loading time and, when auto-load is used, the per-crate fee.</summary>
    public int BoxCount
    {
        get
        {
            int n = 0;
            foreach (var p in Picks) n += p.Count;
            return n;
        }
    }

    /// <summary>True when the picks reach the recommended quantity exactly.</summary>
    public bool HitsTarget => ShortfallScu == 0;

    /// <summary>True when the kiosk sells nothing this ship can load within the target at all.</summary>
    public bool Empty => Picks.Count == 0;

    /// <summary>True when the terminal offers only one usable size, so a min/max reading would
    /// print the same number twice.</summary>
    public bool SingleSize => MinContainerScu == MaxContainerScu;
}

/// <summary>
/// Chooses the crate sizes to buy at one terminal. Pure: every input is a plain value, no snapshot
/// and no I/O, so the whole decision is exercised headless.
///
/// The rule is "get as close to the target as possible without going over, and among equally full
/// loads use the fewest crates". Fewest crates is not cosmetic: loading time scales with crate
/// count and Star Citizen's auto-load fee is charged per crate (a datamined ladder of
/// 30/55/100/190/360/495/680 aUEC for 1/2/4/8/16/24/32), so 21x32 beats 84x8 for the same 672 SCU.
///
/// This is a coin problem, not a division. A gcd shortcut would be wrong: a kiosk offering only
/// 24 and 32 has gcd 8 but cannot make 8, 16 or 40. The bounded DP below is exact, and cheap -
/// the largest hull in the catalog is 4,608 SCU against at most seven crate sizes.
///
/// DELIBERATE LIMIT: this reasons about CAPACITY, not geometry. It answers "which crates add up",
/// not "do those crates physically fit that ship's grid layout", which is the Cargo Planner's job.
/// A ship's max container size is honoured, so a crate too large for any of its grids is never
/// suggested.
/// </summary>
public static class ContainerPlanner
{
    /// <summary>
    /// Plan a purchase at a terminal. <paramref name="containerSizes"/> is the terminal's crate menu
    /// in the market snapshot's own comma-separated form ("1,2,4,8,16,24,32"), which varies per
    /// terminal for the same commodity. Returns null when nothing can be planned: no target, no
    /// usable ship capacity, or an unparseable menu (an unknown menu is never guessed at).
    /// </summary>
    public static ContainerPlan? Plan(string containerSizes, int shipMaxContainerScu, int targetScu)
    {
        if (targetScu <= 0 || shipMaxContainerScu <= 0) return null;

        var sizes = UsableSizes(containerSizes, shipMaxContainerScu);
        if (sizes.Count == 0) return null;

        // boxes[i] = fewest crates that total exactly i SCU; -1 = unreachable.
        // pick[i] = a crate size used in that best solution, for reconstruction.
        var boxes = new int[targetScu + 1];
        var pick = new int[targetScu + 1];
        for (int i = 1; i <= targetScu; i++) boxes[i] = -1;

        for (int i = 1; i <= targetScu; i++)
            foreach (var s in sizes)
            {
                if (s > i) continue;
                var prev = boxes[i - s];
                if (prev < 0) continue;
                if (boxes[i] < 0 || prev + 1 < boxes[i]) { boxes[i] = prev + 1; pick[i] = s; }
            }

        // Fullest reachable load at or under the target. Always terminates: 0 is reachable, and a
        // total of 0 is a real answer meaning "the smallest crate here is bigger than the target".
        int best = targetScu;
        while (best > 0 && boxes[best] < 0) best--;

        var counts = new SortedDictionary<int, int>(Comparer<int>.Create((a, b) => b.CompareTo(a)));
        for (int at = best; at > 0; at -= pick[at])
            counts[pick[at]] = counts.TryGetValue(pick[at], out var n) ? n + 1 : 1;

        var picks = new List<ContainerPick>(counts.Count);
        foreach (var kv in counts) picks.Add(new ContainerPick(kv.Key, kv.Value));
        // The usable range is reported from the SAME filtered set the plan was built from, so the
        // "min/max size for purchase" a caller displays can never contradict the containers
        // recommended under it. Reading it off the raw menu instead would advertise a size this
        // hull cannot load - sizes is sorted descending.
        return new ContainerPlan(best, targetScu - best, picks, sizes[^1], sizes[0]);
    }

    /// <summary>
    /// How much of <paramref name="targetScu"/> can actually be bought here, and nothing else about
    /// how. The route planner ranks on this, so a route is priced on cargo that can be loaded rather
    /// than on the raw capacity of the hull (issue #31: "do not recommend a manifest size that can
    /// not be achieved by those sizes").
    /// <para>
    /// Delegates to <see cref="Plan"/> rather than restating the coin math, so the quantity a route
    /// is ranked on and the containers its card lists are one decision and can never contradict each
    /// other on screen.
    /// </para>
    /// <para>
    /// Fails OPEN: when nothing can be planned at all (no target, no usable ship capacity, an
    /// unparseable menu) the target is returned unchanged, because a route must not be made to look
    /// worse for a measurement that could not be taken. RoutePlanner's BoxFits gate already excludes
    /// the unusable-menu and zero-capacity cases before this is reached, so in ranking the fallback
    /// only ever fires on a target that is already zero.
    /// </para>
    /// </summary>
    public static int BuyableScu(string containerSizes, int shipMaxContainerScu, int targetScu) =>
        Plan(containerSizes, shipMaxContainerScu, targetScu)?.TotalScu ?? targetScu;

    /// <summary>
    /// The crate sizes a given ship can actually take from a given terminal: the terminal's menu
    /// filtered to what fits the ship's largest grid. Same split discipline as TradeMath.BoxFits
    /// (comma-separated, trimmed, empties dropped), and the same fail-closed stance - a size that
    /// does not parse as a positive integer is dropped rather than assumed.
    /// </summary>
    private static List<int> UsableSizes(string containerSizes, int shipMaxContainerScu)
    {
        var sizes = new List<int>();
        if (string.IsNullOrWhiteSpace(containerSizes)) return sizes;
        foreach (var token in containerSizes.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            if (int.TryParse(token, out var size) && size > 0 && size <= shipMaxContainerScu && !sizes.Contains(size))
                sizes.Add(size);
        sizes.Sort((a, b) => b.CompareTo(a));   // largest first: the DP prefers fewer crates anyway,
        return sizes;                            // but this makes reconstruction deterministic
    }
}
