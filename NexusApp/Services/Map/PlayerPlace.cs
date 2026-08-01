using NexusApp.Models;

namespace NexusApp.Services.Map;

/// <summary>
/// "Where is the player, and how far is that from X." One small read-only seam over the geometry
/// catalog and the Game.log location timeline, so any surface can ask - before this, App.Locations
/// was read by exactly two views and every other page was position-blind even though the data was
/// sitting in memory.
///
/// Deliberately holds NO state of its own and caches nothing: LocationTracker is the single source
/// of truth and updates on its own schedule, so every property here reads through on each call. That
/// keeps this correct without a subscription, an invalidation rule, or a stale-cache bug.
///
/// <para>Current == null is a FIRST-CLASS answer meaning "we do not know", not an error. It is the
/// normal state with Star Citizen closed, before the first boundary crossing of a session, and
/// whenever the log names somewhere the catalog cannot place. Every caller must render it as
/// silence - no placeholder, no zero, no "unknown" chip - matching the absent-not-placeholder rule
/// the trade surfaces already follow.</para>
/// </summary>
public sealed class PlayerPlace
{
    private readonly MapCatalog _map;
    private readonly LocationTracker _locations;

    public PlayerPlace(MapCatalog map, LocationTracker locations)
    {
        _map = map;
        _locations = locations;
    }

    /// <summary>The map object the player was last seen at, or null when nothing resolves. Resolved
    /// through the catalog's raw-token tier, so gateway names that repeat across systems land in the
    /// right one.</summary>
    public MapObject? Current => _map.ResolvePlayerLocation(_locations.LastKnownLocation, _locations.LastKnownRawToken);

    /// <summary>The display name the log gave, even when it does not resolve to an object - a
    /// jurisdiction like "Rough and Ready", or a gateway with no captured token. Callers that want to
    /// SAY where the player is should prefer this; callers that want to MEASURE need Current.</summary>
    public string? Label => _locations.LastKnownLocation;

    /// <summary>When that reading was taken. The timeline is sparse and boundary-driven by nature, so
    /// anything shown to the user should be dated rather than implied to be live.</summary>
    public DateTime? SeenUtc => _locations.LastSeenUtc;

    /// <summary>The UEX Location string for the current place, when one is known. This is the hint
    /// that lets a UEX lookup succeed at a gateway whose in-game name UEX does not use.</summary>
    public string? UexLocation => _locations.LastKnownUexLocation;

    /// <summary>The star system the player is in, or null when unknown.</summary>
    public string? System => Current?.System;

    /// <summary>Formatted straight-line distance from the player to <paramref name="target"/>, or
    /// null when either end is unknown or the two are in different systems. Null is the honest answer
    /// across a jump point: that route is not Euclidean and a number would be a lie.</summary>
    public string? DistanceFrom(MapObject? target)
        => _map.DistanceMeters(Current, target) is { } m ? MapCatalog.FormatGm(m) : null;

    /// <summary>The same, for a market terminal. Resolves the terminal through the geometry-only
    /// path, so a terminal at a place held off the map surface still reports a real distance.</summary>
    public string? DistanceFrom(MarketTerminal? terminal)
        => DistanceFrom(_map.ResolveTerminalForDistance(terminal));
}
