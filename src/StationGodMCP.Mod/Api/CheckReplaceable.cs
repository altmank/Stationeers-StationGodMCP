#nullable enable

using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.Objects;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Shared.Game.Build;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;

namespace StationGodMCP.Api;

/// <summary>
/// check_replaceable: for each thing, could a player place it again exactly where it stands, with every neighbour
/// present (PlayerPlacement.AsItStands: the game's cursor checks for its prefab at its position and turn, the thing
/// itself treated as gone). The question a blueprint validator asks of every pasted piece: BlueprintMod spawns
/// without the cursor's checks, so a floating vent pastes and works but can never be rebuilt. Read only.
/// </summary>
internal static class CheckReplaceableApi
{
    internal const int MaximumIds = 1024;

    internal static CheckReplaceableView Handle(Args args)
    {
        if (GridController.World == null)
        {
            throw ApiErrors.Refused("not_ready", "The world grid is not loaded.");
        }

        List<ThingId> ids = args.ThingIds("reference_ids", MaximumIds);
        BuildCatalogue catalogue = BuildCatalogue.Load();
        List<ReplaceableView> results = new List<ReplaceableView>(ids.Count);
        foreach (ThingId id in ids)
        {
            results.Add(One(id, catalogue));
        }

        return new CheckReplaceableView(results);
    }

    private static ReplaceableView One(ThingId id, BuildCatalogue catalogue)
    {
        if (!GameLookup.TryFindThing(id, out Thing thing) || thing.IsBeingDestroyed)
        {
            return new ReplaceableView(id, null, PlacementVerdict.Unchecked($"no thing has reference id {id}"));
        }

        return thing is Structure structure
            ? new ReplaceableView(id, thing.PrefabName, PlayerPlacement.AsItStands(structure, catalogue))
            : new ReplaceableView(id, thing.PrefabName,
                PlacementVerdict.Unchecked($"{thing.PrefabName} is not a structure; items are not placed"));
    }
}
