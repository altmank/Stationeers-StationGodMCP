#nullable enable

using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure.Verbs;
using UnityEngine;

namespace StationGodMCP.Verbs;

/// <summary>
/// find ore (catalogue find.json): the deep-mining profile at a point (near, default where the player stands) and the
/// world's profiles; with ores, the limit nearest spots within radius whose profile holds every ore, at least
/// separation apart, sampled every step metres (DeepMinerSpots); {at, profiles, search, spots, beacons}. Read only.
/// </summary>
internal static class FindOre
{
    internal static VerbOutcome Run(FindOreOptions options)
    {
        Vector3 centre = options.Selector.Near is NearPoint.At at
            ? new Vector3((float)at.Point.X, (float)at.Point.Y, (float)at.Point.Z)
            : PlayerOrigin.RequireHuman().Position;
        double radius = options.Selector.Radius ?? FindOreOptions.Spec.Option("radius")?.DefaultNumber ?? 0.0;
        return WorkResult<DeepMinerSpotsView>.Of(answer => DeepMinerSpots.Reporting(centre, options.Ores, radius,
                options.Paging.Limit ?? int.MaxValue, options.Separation ?? 0.0, options.Step, answer),
            found => new VerbOutcome.Answered(ReplyFields.Of(("at", found.At), ("profiles", found.Profiles),
                ("search", found.Search), ("spots", found.Spots), ("beacons", found.Beacons))));
    }
}
