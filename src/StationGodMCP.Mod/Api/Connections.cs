#nullable enable

using System;
using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.Atmospherics;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Localization2;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Pipes;
using Networks;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Shared.Game.Upgrades;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Profiling;
using StationGodMCP.Pure.Protocol;

namespace StationGodMCP.Api;

/// <summary>
/// connections: every connection end of a thing and what is attached there (EndsReader). Read only.
/// </summary>
internal static class ConnectionsApi
{
    [Profiled]
    internal static object Handle(Args args)
    {
        if (!args.Has("reference_id"))
        {
            throw ApiErrors.InvalidArgument(
                Vocabulary.Text("Pass {arg:reference_id}, {arg:network_id} with {arg:kind}, or {arg:min} and " +
                    "{arg:max} (open ends in a box)."));
        }

        args.Reject("{arg:reference_id}", "kind", "limit", "offset", "prefab_contains", "open_ends_only", "min",
            "max", "near", "radius_m", "summarize");
        return EndsReader.Read(GameLookup.RequireThing(args.ThingId("reference_id")));
    }
}
