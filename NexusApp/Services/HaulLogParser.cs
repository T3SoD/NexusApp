using System.Text.RegularExpressions;
using NexusApp.Models;

namespace NexusApp.Services;

// NOTE ON THE MARKER'S position [x,y,z], which this record deliberately no longer carries.
//
// The coordinates are NOT universe coordinates and cannot be compared with the geometry catalog's.
// They are relative to the marker's own zone host, and the line names that host in a separate
// zoneHostId field. Three real markers from one Stanton session, 2026-07-31, each with a different
// zoneHostId, make it unarguable:
//
//   zoneHostId [729969381133]  position [x: 42908.6,        y: 28314.8,        z: -360.7]
//   zoneHostId [729968885546]  position [x: 18588852971.4,  y: -22152678616.8, z: 2691106.8]
//   zoneHostId [729968971474]  position [x: 0.0,            y: 0.0,            z: 0.0]
//
// Six orders of magnitude apart, and one sitting exactly on its container's origin. Converting any
// of them into the frame the map uses would need a zoneHostId -> container map plus the container's
// own transform, and no such mapping exists anywhere in the app or its data.
//
// So the fields were removed (app review G13: written on every leg, read by nothing) instead of
// being wired into a distance. Anything that placed a haul stop from these numbers would produce a
// confident, wrong answer - the worst possible failure for a distance. Haul stops are placed by
// NAME through the geometry catalog, like every other stop. The regex below still MATCHES the
// position block, because it is a reliable part of the line's shape; it just no longer captures it.
public record MarkerInfo(string MissionId, string Generator, string Contract, HaulRole Role,
                         string CargoKey, int LegIndex, string ObjectiveId);
public record DeliverInfo(string MissionId, string ObjectiveId, string Commodity, int TargetScu, string Destination);
public record AcceptInfo(string MissionId, string Title);
public record CompletedInfo(string MissionId, string ObjectiveId);
public record EndInfo(string MissionId, HaulOutcome Outcome);

// Stateless parsing of the haul-relevant Game.log line shapes. No PII: EndMission's
// Player[..]/PlayerId[..] are never read. The only data dependency is the embedded datamined
// contract catalog, consulted for haul-ness. See HaulLogParserTests for the line fixtures.
public static class HaulLogParser
{
    private static readonly Regex Marker = new(
        @"<CLocalMissionPhaseMarker::CreateMarker>.*?missionId \[(?<mid>[0-9a-f-]+)\].*?" +
        @"generator name \[(?<gen>[^\]]+)\].*?contract \[(?<contract>[^\]]+)\].*?" +
        @"objectiveId \[(?<role>pickup|dropoff)_(?<key>[0-9a-f-]+_(?<leg>\d+))\].*?" +
        @"position \[x: [-0-9.]+, y: [-0-9.]+, z: [-0-9.]+\]",
        RegexOptions.Compiled);

    private static readonly Regex Deliver = new(
        @"New Objective: Deliver \d+/(?<scu>\d+) SCU of (?<commodity>.+?) to (?<dest>.+?): "" .*?" +
        @"MissionId: \[(?<mid>[0-9a-f-]+)\], ObjectiveId: \[(?<oid>dropoff_[0-9a-f-]+_\d+)\]",
        RegexOptions.Compiled);

    private static readonly Regex Accept = new(
        @"Contract Accepted:\s+(?<title>.+?): "" .*?MissionId: \[(?<mid>[0-9a-f-]+)\]",
        RegexOptions.Compiled);

    private static readonly Regex Completed = new(
        @"<ObjectiveUpserted>.*?mission_id (?<mid>[0-9a-f-]+) - objective_id (?<oid>\S+) " +
        @"- state MISSION_OBJECTIVE_STATE_COMPLETED",
        RegexOptions.Compiled);

    private static readonly Regex End = new(
        @"<EndMission>.*?MissionId\[(?<mid>[0-9a-f-]+)\].*?CompletionType\[(?<ct>\w+)\]",
        RegexOptions.Compiled);

    private static readonly Regex EmTag = new(@"</?EM\d*>", RegexOptions.Compiled);

    // Cheap pre-filter so the tracker can skip the bulk of lines before running regex.
    // CLocalMissionPhaseMarker is included on its own because some haul families' contract
    // tokens (HH_*, CleanAir_*) contain neither "HaulCargo" nor "Hauling"; ParseMarker then
    // decides haul-ness per contract (issue #51).
    public static bool LooksHaulRelevant(string raw) =>
        raw.Contains("HaulCargo") || raw.Contains("Hauling") ||
        raw.Contains("CLocalMissionPhaseMarker") ||
        raw.Contains("SCU of") || raw.Contains("Contract Accepted") ||
        raw.Contains("ObjectiveUpserted") || raw.Contains("EndMission");

    public static MarkerInfo? ParseMarker(string raw)
    {
        var m = Marker.Match(raw);
        if (!m.Success) return null;
        // Haul families: HaulCargo_* (Stanton), *CargoHauling* (RedWind), *_Hauling (CFP / Citizens For
        // Prosperity), plus families whose tokens carry neither substring but sit in the datamined
        // contract catalog (HH_* Headhunters, CleanAir_*). Missions that also use pickup/dropoff markers
        // but are NOT hauls (RecoverCargo, Hockrow facility delve, Shubin mining) match none of these.
        if (!IsHaulContract(m.Groups["contract"].Value)) return null;
        var role = m.Groups["role"].Value == "pickup" ? HaulRole.Pickup : HaulRole.Dropoff;
        return new MarkerInfo(
            m.Groups["mid"].Value, m.Groups["gen"].Value, m.Groups["contract"].Value, role,
            m.Groups["key"].Value, int.Parse(m.Groups["leg"].Value),
            $"{m.Groups["role"].Value}_{m.Groups["key"].Value}");
    }

    public static DeliverInfo? ParseDeliver(string raw)
    {
        var m = Deliver.Match(raw);
        if (!m.Success) return null;
        return new DeliverInfo(m.Groups["mid"].Value, m.Groups["oid"].Value,
            m.Groups["commodity"].Value.Trim(), int.Parse(m.Groups["scu"].Value),
            m.Groups["dest"].Value.Trim());
    }

    public static AcceptInfo? ParseContractAccepted(string raw)
    {
        var m = Accept.Match(raw);
        if (!m.Success) return null;
        var title = EmTag.Replace(m.Groups["title"].Value, "").Replace("  ", " ").Trim();
        return new AcceptInfo(m.Groups["mid"].Value, title);
    }

    public static CompletedInfo? ParseObjectiveCompleted(string raw)
    {
        var m = Completed.Match(raw);
        return m.Success ? new CompletedInfo(m.Groups["mid"].Value, m.Groups["oid"].Value) : null;
    }

    public static EndInfo? ParseEndMission(string raw)
    {
        var m = End.Match(raw);
        if (!m.Success) return null;
        var outcome = m.Groups["ct"].Value switch
        {
            "Complete"   => HaulOutcome.Complete,
            "Abandon"    => HaulOutcome.Abandoned,
            "Fail"       => HaulOutcome.Failed,
            "Deactivate" => HaulOutcome.Deactivated,
            _            => HaulOutcome.Active,
        };
        return new EndInfo(m.Groups["mid"].Value, outcome);
    }

    public static string ParseTopology(string contract)
    {
        var c = contract.ToLowerInvariant();
        if (c.Contains("singletomulti2")) return "1 to 2";
        if (c.Contains("singletomulti3")) return "1 to 3";
        if (c.Contains("multi2tosingle")) return "2 to 1";
        if (c.Contains("atob")) return "1 to 1";
        return "Unknown";
    }

    // Generators whose derived name is not the contractor the game shows. The display names come
    // from the datamined contract generator records and localization strings: HeadHunters_Generator
    // camel-splits to "Head Hunters" but the game says "Headhunters"; the CleanAir event generator's
    // hauling handler contracts for Ling Family Hauling; TheBackpocket's ORS hauls contract for
    // Covalex Independent Contractors.
    private static readonly Dictionary<string, string> GeneratorCompanies = new(StringComparer.Ordinal)
    {
        ["HeadHunters_Generator"] = "Headhunters",
        ["CleanAir"] = "Ling Family Hauling",
        ["TheBackpocket"] = "Covalex Independent Contractors",
    };

    public static string CompanyDisplay(string generator)
    {
        if (GeneratorCompanies.TryGetValue(generator, out var known)) return known;
        var name = generator.Replace("_Hauling", "").Replace("Hauling", "")
                            .Replace("_Generator", "").Replace('_', ' ').Trim();
        // Split runs like "RedWind" -> "Red Wind".
        name = Regex.Replace(name, "(?<=[a-z])(?=[A-Z])", " ");
        return string.IsNullOrWhiteSpace(name) ? generator : name;
    }

    // A contract is a haul when its token carries a hauling family substring, or when the
    // datamined contract catalog knows it (HH_*, CleanAir_* and friends carry neither substring
    // yet are cargo hauls with datamined container caps; issue #51). The catalog is embedded
    // data, so this stays deterministic and offline.
    private static bool IsHaulContract(string contract) =>
        contract.Contains("HaulCargo", StringComparison.OrdinalIgnoreCase) ||
        contract.Contains("Hauling", StringComparison.OrdinalIgnoreCase) ||
        ContractCapCatalog.Instance.Lookup(contract).HasValue;
}
