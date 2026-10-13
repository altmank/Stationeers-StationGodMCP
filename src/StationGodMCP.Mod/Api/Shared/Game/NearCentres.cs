#nullable enable

using Assets.Scripts.Objects;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Verbs;
using UnityEngine;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// Where a selector's near points (Selector.Near): a point as given, the player where it stands, the thing under the
/// crosshair, or a thing by id, where its outermost holder is. Refused (no_local_player, no_view, thing_not_found)
/// when there is no such place.
/// </summary>
internal static class NearCentres
{
    internal static Vec3 Of(NearPoint near, string label) => near switch
    {
        NearPoint.At at => at.Point,
        NearPoint.Player => Point(PlayerOrigin.RequireHuman().Position),
        NearPoint.LookingAt => Point(HolderChain.PlaceOf(Crosshair(label)).Position),
        NearPoint.Thing thing => Point(HolderChain.PlaceOf(Live(thing.Id, label)).Position),
        _ => throw new System.InvalidOperationException("Unknown near point.")
    };

    /// <summary>
    /// Where a list's selector keeps things: near "player" with radius is a distance from the player (any size), any
    /// other near a sphere of radius metres around the point it names, box the box, neither anywhere.
    /// </summary>
    internal static SelectorPlace Place(Selector selector, string label) =>
        selector.Near is { } near and not NearPoint.Player && selector.Radius is { } radius
            ? new SelectorPlace(null, PointArea.Near(Of(near, label), radius))
            : new SelectorPlace(selector.AroundPlayerM, selector.AreaAround(null));

    private static Thing Crosshair(string label)
    {
        Thing? thing = PlayerView.Current().Require(label).Target;
        return thing != null
            ? thing
            : throw ApiErrors.Refused("thing_not_found", $"{label}: nothing is under the crosshair for near \"looking_at\".");
    }

    private static Thing Live(string id, string label)
    {
        Thing? thing = long.TryParse(id, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out long referenceId)
            ? Thing.Find(referenceId)
            : null;
        return thing != null && !thing.IsBeingDestroyed
            ? thing
            : throw ApiErrors.Refused("thing_not_found", $"{label}: near names thing {id}, which does not exist.");
    }

    private static Vec3 Point(Vector3 position) => new Vec3(position.x, position.y, position.z);
}

/// <summary>A selector's place: metres around the player (near "player"), and the area things must stand in.</summary>
internal sealed class SelectorPlace
{
    internal SelectorPlace(double? aroundPlayerM, PointArea area)
    {
        AroundPlayerM = aroundPlayerM;
        Area = area;
    }

    public double? AroundPlayerM { get; }

    public PointArea Area { get; }
}
