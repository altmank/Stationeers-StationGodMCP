#nullable enable

using Assets.Scripts;
using Assets.Scripts.Objects;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Shared.Game.Build;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>
/// check replaceable (catalogue check.json): for each thing, could a player place it again exactly where it stands,
/// with every neighbour present (PlayerPlacement.AsItStands: the game's cursor checks for its prefab at its position
/// and turn, the thing itself treated as gone). The question a blueprint validator asks of every pasted piece:
/// BlueprintMod spawns without the cursor's checks, so a floating vent pastes and works but can never be rebuilt. One
/// row per id ({index, ok, reference_id, prefab_name, replaceable, rule, reason}), an id no thing has a row with
/// error; the build catalogue is read once for the call. Read only.
/// </summary>
internal static class CheckReplaceable
{
    internal const int MaximumIds = 1024;

    internal static VerbOutcome Run(CheckReplaceableOptions options)
    {
        string label = CheckReplaceableOptions.Spec.Label;
        PerIdList ids = options.Selector.Ids!;
        if (ids.Ids.Count > MaximumIds)
        {
            return new VerbOutcome.Refused(OptionProblem.Refusal(label,
                new[] { new OptionProblem("id", $"takes at most {MaximumIds} things") }));
        }

        if (GridController.World == null)
        {
            throw ApiErrors.Refused("not_ready", $"{label}: the world grid is not loaded.");
        }

        BuildCatalogue catalogue = BuildCatalogue.Load();
        return IdAnswers.Things(ids, label, id => One(id, catalogue));
    }

    private static ReplaceableView One(ThingId id, BuildCatalogue catalogue)
    {
        Thing thing = GameLookup.RequireThing(id);
        if (thing.IsBeingDestroyed)
        {
            throw ApiErrors.ThingNotFound(id);
        }

        return new ReplaceableView(thing.PrefabName, thing is Structure structure
            ? PlayerPlacement.AsItStands(structure, catalogue)
            : PlacementVerdict.Unchecked($"{thing.PrefabName} is not a structure; items are not placed"));
    }
}
