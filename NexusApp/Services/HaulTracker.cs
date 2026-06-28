using NexusApp.Models;

namespace NexusApp.Services;

// BETA. App-lifetime owner of a Game.log watcher dedicated to cargo-hauling missions.
// Mirrors GameLogSession's shape (its own watcher; public Ingest for headless tests). Reads a
// game-authored file (read-only) and never extracts player identity. A mission id becomes a
// "haul" only when a HaulCargo/CargoHauling marker or a "Deliver N SCU" objective is seen; the
// generic ObjectiveUpserted/EndMission lines are applied only to known hauls so bounty/combat
// missions are ignored.
public sealed class HaulTracker : IDisposable
{
    private readonly GameLogWatcher _watcher = new();
    private readonly Dictionary<string, Haul> _byId = new();
    private readonly List<Haul> _order = new();   // insertion order for display
    private string _currentShardId = "";          // last shard seen, to clear hauls on a shard change
    private readonly Dictionary<string, ContractDetails> _pendingByOrg = new();   // OCR contractor org -> detail, applied when its haul appears

    public HaulTracker()
    {
        _watcher.LineAppended += Ingest;
        _watcher.LogReset += Reset;
    }

    public string PreferredPath { get; set; } = "";
    public bool IsRunning => _watcher.IsRunning;
    public string Path => _watcher.Path;

    public string StartPath()
    {
        if (!string.IsNullOrEmpty(Path) && System.IO.File.Exists(Path)) return Path;
        if (!string.IsNullOrEmpty(PreferredPath)) return PreferredPath;
        return GameLogWatcher.FindGameLog();
    }

    // Replay the current Game.log from the top so an app restart mid-session rebuilds active
    // hauls (parsing is idempotent: markers dedupe by objectiveId).
    public void Start(string path, bool fromBeginning = true) => _watcher.Start(path, fromBeginning);
    public void Stop() => _watcher.Stop();

    public IReadOnlyList<Haul> AllHauls => _order;
    public IReadOnlyList<Haul> ActiveHauls => _order.FindAll(h => h.IsActive);
    public IReadOnlyList<Haul> FinishedHauls => _order.FindAll(h => !h.IsActive);

    public event Action? Changed;
    public event Action<Haul>? HaulEnded;
    /// <summary>Raised the first time a haul gets OCR contract detail attached (for a toast / indicator).</summary>
    public event Action<Haul>? ContractPaired;

    public void Reset() => ClearInternal();   // new SC session (Game.log reset)

    /// <summary>User-requested clear of all hauls, active and finished (the Clear-all button).</summary>
    public void ClearAll()
    {
        if (_order.Count == 0) return;
        Logger.Info("[HAUL] cleared all hauls");
        ClearInternal();
    }

    /// <summary>Delete one haul by mission id (the per-mission x button).</summary>
    public void Remove(string missionId)
    {
        if (_byId.Remove(missionId, out var h))
        {
            _order.Remove(h);
            Logger.Info($"[HAUL] removed haul {h.Company}");
            Changed?.Invoke();
        }
    }

    private void ClearInternal()
    {
        _byId.Clear();
        _order.Clear();
        _pendingByOrg.Clear();
        Changed?.Invoke();
    }

    // Groups incomplete legs of all active hauls by location: where to load (Pickups) and
    // where to drop (Dropoffs). Pickup commodity/SCU is borrowed from the sibling dropoff
    // that shares the same CargoKey (same physical cargo, different direction).
    public Consolidation BuildConsolidation()
    {
        var con = new Consolidation();
        var pickups = new Dictionary<string, ConsolidationStop>();
        var dropoffs = new Dictionary<string, ConsolidationStop>();

        foreach (var h in _order)
        {
            if (!h.IsActive) continue;

            foreach (var leg in h.Legs)
            {
                if (leg.Completed) continue;

                if (leg.Role == HaulRole.Dropoff && leg.TargetScu > 0)
                    AddItem(dropoffs, leg.Destination, leg.Commodity, leg.TargetScu, h.MissionId);

                if (leg.Role == HaulRole.Pickup)
                {
                    // Borrow commodity/SCU from the sibling dropoff that shares this CargoKey.
                    var sib = h.Legs.Find(l => l.Role == HaulRole.Dropoff && l.CargoKey == leg.CargoKey);
                    var name = string.IsNullOrWhiteSpace(h.PickupName) ? "Pickup (TBD)" : h.PickupName;
                    AddItem(pickups, name, sib?.Commodity ?? "", sib?.TargetScu ?? 0, h.MissionId);
                }
            }
        }

        con.Pickups.AddRange(pickups.Values);
        con.Dropoffs.AddRange(dropoffs.Values);
        return con;

        static void AddItem(Dictionary<string, ConsolidationStop> map, string loc, string commodity, int scu, string mid)
        {
            if (string.IsNullOrWhiteSpace(loc)) return;
            if (!map.TryGetValue(loc, out var stop)) { stop = new ConsolidationStop { Location = loc }; map[loc] = stop; }
            stop.Items.Add((commodity, scu, mid));
        }
    }

    public void Ingest(GameLogEntry e)
    {
        var raw = e.Raw;

        // Leaving the PU entirely (menu / quit / disconnect) abandons your in-game contracts, so clear
        // all hauls on the EAC EndSession - the same signal the shard tracker uses to drop the "current"
        // shard. This line does NOT pass LooksHaulRelevant, so it must be handled before that filter.
        if (raw.Contains("CDisciplineServiceExternal::EndSession"))
        {
            _currentShardId = "";
            if (_order.Count > 0) { Logger.Info("[HAUL] shard exit - cleared hauls"); ClearInternal(); }
            return;
        }

        // Missions are shard-specific: changing shard/server abandons your contracts in-game, so a new
        // shard clears all hauls (active and finished). Reuses the shard parser to read the join line.
        if (raw.Contains("<Join PU>"))
        {
            var shardId = ShardLogParser.ParseJoin(raw)?.ShardId;
            if (shardId is not null && shardId != _currentShardId)
            {
                _currentShardId = shardId;
                if (_order.Count > 0) { Logger.Info("[HAUL] shard changed - cleared hauls"); ClearInternal(); }
            }
            return;
        }

        if (!HaulLogParser.LooksHaulRelevant(raw)) return;

        var marker = HaulLogParser.ParseMarker(raw);
        if (marker is not null) { ApplyMarker(marker); return; }

        var deliver = HaulLogParser.ParseDeliver(raw);
        if (deliver is not null) { ApplyDeliver(deliver); return; }

        var accept = HaulLogParser.ParseContractAccepted(raw);
        if (accept is not null && _byId.TryGetValue(accept.MissionId, out var ah))
        {
            ah.RouteTitle = accept.Title;
            TryApplyPending(ah);
            ah.PickupName = DerivePickup(accept.Title);
            Changed?.Invoke();
            return;
        }

        var completed = HaulLogParser.ParseObjectiveCompleted(raw);
        if (completed is not null && _byId.TryGetValue(completed.MissionId, out var ch))
        {
            var leg = ch.LegByObjective(completed.ObjectiveId);
            if (leg is not null)
            {
                leg.Completed = true;
                Logger.Info($"[HAUL] leg complete: {ch.Company} {leg.Role} {leg.Commodity}");
                Changed?.Invoke();
            }
            return;
        }

        var end = HaulLogParser.ParseEndMission(raw);
        if (end is not null && _byId.TryGetValue(end.MissionId, out var eh))
        {
            eh.Outcome = end.Outcome;
            Logger.Info($"[HAUL] mission ended: {eh.Company} {eh.Topology} -> {end.Outcome}");
            HaulEnded?.Invoke(eh);
            Changed?.Invoke();
        }
    }

    private Haul GetOrCreate(string missionId)
    {
        if (_byId.TryGetValue(missionId, out var h)) return h;
        h = new Haul { MissionId = missionId };
        _byId[missionId] = h;
        _order.Add(h);
        return h;
    }

    private void ApplyMarker(MarkerInfo m)
    {
        var existed = _byId.ContainsKey(m.MissionId);
        var h = GetOrCreate(m.MissionId);
        h.Company = HaulLogParser.CompanyDisplay(m.Generator);
        h.ContractName = m.Contract;
        h.Topology = HaulLogParser.ParseTopology(m.Contract);
        if (h.LegByObjective(m.ObjectiveId) is null)
            h.Legs.Add(new HaulLeg
            {
                ObjectiveId = m.ObjectiveId, CargoKey = m.CargoKey, Role = m.Role,
                LegIndex = m.LegIndex, X = m.X, Y = m.Y, Z = m.Z,
            });
        if (!existed) Logger.Info($"[HAUL] mission accepted: {h.Company} {h.Topology}");
        TryApplyPending(h);   // the haul's company is now known; apply any contract scanned before it appeared
        Changed?.Invoke();
    }

    private void ApplyDeliver(DeliverInfo d)
    {
        var h = GetOrCreate(d.MissionId);
        var leg = h.LegByObjective(d.ObjectiveId);
        if (leg is null)
        {
            leg = new HaulLeg { ObjectiveId = d.ObjectiveId, Role = HaulRole.Dropoff };
            h.Legs.Add(leg);
        }
        leg.Commodity = d.Commodity;
        leg.TargetScu = d.TargetScu;
        leg.Destination = d.Destination;
        Changed?.Invoke();
    }

    // Best-effort: the left side of an "A > B" route title is the pickup location. Flavor titles
    // ("Red Wind Seeking New Haulers") have no route, so pickup name stays empty (handled in consolidation).
    private static string DerivePickup(string title)
    {
        var idx = title.IndexOf('>');
        if (idx <= 0) return "";
        var left = title[..idx];
        var bar = left.LastIndexOf('|');
        if (bar >= 0) left = left[(bar + 1)..];
        return left.Trim();
    }

    /// <summary>Attach OCR'd contract detail to the matching active haul. Matching the OCR title to the
    /// log title is hopeless (the panel's OCR reading order scrambles it), so we join on the contractor:
    /// the OCR org ("Ling Family Hauling") contains the log company ("Ling Family"). When several active
    /// hauls share a contractor (e.g. two Red Wind hauls), disambiguate by cargo - the log gives each
    /// haul its commodity/SCU. If cargo still can't tell them apart, skip rather than mis-tag.</summary>
    public void ApplyContractDetails(ContractDetails d)
    {
        var org = ContractParser.NormalizeTitle(d.ContractedBy);
        if (org.Length == 0) return;   // no contractor -> no reliable join key

        var candidates = _order.FindAll(h => h.IsActive && h.Company.Length > 0
                                             && org.Contains(ContractParser.NormalizeTitle(h.Company)));
        if (candidates.Count == 0) { _pendingByOrg[org] = d; return; }   // haul not accepted yet; applied later

        var target = candidates.Count == 1 ? candidates[0] : DisambiguateByCargo(candidates, d);
        if (target is null) return;   // ambiguous and cargo can't separate them -> skip

        ApplyAndNotify(target, d);
        Changed?.Invoke();
    }

    // Several active hauls share a contractor (e.g. three Red Wind hauls). Place the scanned contract:
    //   1) on the haul whose LOG legs carry this commodity (strongest, when the log knows it);
    //   2) else on a haul ALREADY paired with this same cargo, so re-scans update it instead of duplicating;
    //   3) else - the log can't tell these hauls apart (no commodity on their legs) - on the first not-yet-
    //      paired one. They are interchangeable in the log, so each scanned contract still lands accurately.
    private static Haul? DisambiguateByCargo(List<Haul> candidates, ContractDetails d)
    {
        if (d.Objectives.Count == 0) return null;   // nothing to place these indistinguishable hauls by

        var byCommodity = candidates.FindAll(h => d.Objectives.TrueForAll(o => HasDropoff(h, o, matchScu: false)));
        if (byCommodity.Count == 1) return byCommodity[0];
        if (byCommodity.Count > 1)
        {
            var byScu = byCommodity.FindAll(h => d.Objectives.TrueForAll(o => HasDropoff(h, o, matchScu: true)));
            if (byScu.Count == 1) return byScu[0];
        }

        var already = candidates.Find(h => SameCargo(h.ContractObjectives, d.Objectives));
        if (already is not null) return already;

        return candidates.Find(h => h.ContractObjectives.Count == 0);
    }

    private static bool HasDropoff(Haul h, ContractObjective o, bool matchScu)
    {
        var commodity = ContractParser.NormalizeTitle(o.Commodity);
        if (commodity.Length == 0) return false;
        return h.Legs.Exists(l => l.Role == HaulRole.Dropoff
            && ContractParser.NormalizeTitle(l.Commodity) == commodity
            && (!matchScu || o.Scu == 0 || l.TargetScu == 0 || l.TargetScu == o.Scu));
    }

    // Two OCR objective sets describe the same cargo when they list the same commodities.
    private static bool SameCargo(List<ContractObjective> have, List<ContractObjective> scanned)
    {
        if (have.Count == 0 || have.Count != scanned.Count) return false;
        return scanned.TrueForAll(o => have.Exists(x =>
            ContractParser.NormalizeTitle(x.Commodity) == ContractParser.NormalizeTitle(o.Commodity)));
    }

    // Enrich, and raise ContractPaired exactly once per haul (on the transition to having detail), so the
    // continuous scanner's repeated re-enriches don't spam the toast/indicator.
    private void ApplyAndNotify(Haul h, ContractDetails d)
    {
        bool wasEnriched = h.Reward > 0 || h.ContractObjectives.Count > 0;
        Enrich(h, d);
        if (!wasEnriched && (h.Reward > 0 || h.ContractObjectives.Count > 0))
        {
            Logger.Info($"[CONTRACT] paired {h.Company} reward {h.Reward}");
            ContractPaired?.Invoke(h);
        }
    }

    // Defensive: never clobber a known value with an empty/zero one from a noisier later scan.
    private static void Enrich(Haul h, ContractDetails d)
    {
        if (d.Reward > 0) h.Reward = d.Reward;
        if (!string.IsNullOrWhiteSpace(d.ContractedBy)) h.ContractedBy = d.ContractedBy;
        if (d.Objectives.Count > 0) h.ContractObjectives = d.Objectives;
    }

    // When a haul first appears (its company becomes known), apply any contract scanned before it existed.
    private void TryApplyPending(Haul h)
    {
        if (_pendingByOrg.Count == 0 || h.Company.Length == 0) return;
        var comp = ContractParser.NormalizeTitle(h.Company);
        string? hit = null;
        foreach (var k in _pendingByOrg.Keys) if (k.Contains(comp)) { hit = k; break; }
        if (hit != null) { ApplyAndNotify(h, _pendingByOrg[hit]); _pendingByOrg.Remove(hit); }
    }

    public void Dispose() => _watcher.Dispose();
}
