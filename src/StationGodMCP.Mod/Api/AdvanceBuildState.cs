#nullable enable

using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.Networking;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Entities;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;

namespace StationGodMCP.Api;

/// <summary>
/// advance_build_state: build a placed structure on to a later build state, paying each state's materials from the
/// sources, as a player's construction does (CODE, Structure.AttackWith): the next state's ToolEntry and ToolEntry2
/// with their quantities are used up, then CurrentBuildStateIndex is raised (its setter updates the cell's air, the
/// build-state sounds and the network flag clients sync by) and UpdateStateVisualizer shows it. The game refuses a
/// damaged structure (CannotConstructWhenDamaged); so does this. Tools are not checked or worn, as place_structure's
/// build_state does not. Writes; dry run by default; host only.
/// </summary>
internal static class AdvanceBuildStateApi
{
    internal static AdvanceBuildStateView Handle(Args args)
    {
        if (NetworkManager.IsClient || !GameManager.RunSimulation)
        {
            throw ApiErrors.Refused("not_host", "This game is a multiplayer client; only the host builds.");
        }

        bool dryRun = WriteMode.IsDryRun(args);
        Thing thing = GameLookup.RequireThing(args.ThingId("reference_id"));
        if (!(thing is Structure structure) || structure.BuildStates == null)
        {
            throw ApiErrors.Refused("no_build_states", $"{Names.Of(thing)} is not a structure with build states.");
        }

        int current = structure.CurrentBuildStateIndex;
        int last = structure.BuildStates.Count - 1;
        int target = args.OptionalInt("to_state", 0, 64) ?? current + 1;
        BuildStepRefusal refusal = BuildStepRule.Refusal(current, structure.BuildStates.Count, target,
            structure.DamageState != null && structure.DamageState.Total > 0f);
        if (refusal != BuildStepRefusal.None)
        {
            throw Refusal(refusal, $"{Names.Of(structure)} {structure.ReferenceId}", current, last);
        }

        bool free = args.OptionalBool("free") ?? false;
        string? freeRefusal = PlacementRule.FreeRefusal(free, WorldManager.IsCreative());
        if (freeRefusal != null)
        {
            throw ApiErrors.Refused("not_creative", $"free: {freeRefusal}.");
        }

        List<ThingId> ids = SourceArgs.Of(args);
        Thing? from = free ? null : SourceOf(ids);
        List<ThingId> missing = new List<ThingId>();
        List<Thing> more = free ? new List<Thing>() : PaySources.Resolve(SourceArgs.Rest(ids), missing);
        if (missing.Count > 0)
        {
            throw ApiErrors.ThingNotFound(missing[0]);
        }

        Dictionary<int, Item> items = new Dictionary<int, Item>();
        List<IReadOnlyList<BuildEntry>> states = BuildMaterials.EntriesOf(structure, items);
        List<ItemStock> stocks = new List<ItemStock>();
        List<BuildCostView> cost = new List<BuildCostView>();
        List<string> problems = new List<string>();
        foreach (ItemCount count in BuildStepRule.Cost(states, current, target))
        {
            Item item = items[count.Item];
            ItemStock stock = free ? ItemStock.Empty(item) : PaySources.StockOf(from, more, item);
            stock.Needed = count.Quantity;
            stocks.Add(stock);
            cost.Add(new BuildCostView(item.PrefabName, Names.Of(item), count.Quantity, free ? (int?)null : stock.Available,
                PaySources.PaidBy(stock)));
            if (!free && stock.Available < count.Quantity)
            {
                problems.Add($"{count.Quantity} {Names.Of(item)} ({item.PrefabName}) needed, {stock.Available} held " +
                             $"by {PaySources.Holders(from!, more)}.");
            }
        }

        List<BuildNeedView> tools = BuildStates.Needs(structure, current, target).FindAll(static need => need.Tool);
        if (!dryRun)
        {
            if (problems.Count > 0)
            {
                throw ApiErrors.Refused("not_enough_materials", string.Join(" ", problems) + " Nothing was built.");
            }

            foreach (ItemStock stock in stocks)
            {
                stock.Take(stock.Needed);
            }

            structure.CurrentBuildStateIndex = target;
            structure.UpdateStateVisualizer();
        }

        return new AdvanceBuildStateView(dryRun, GameLookup.ViewOf(structure), current, target, last, cost, tools,
            problems, dryRun ? null : BuildStates.Of(structure));
    }

    private static ApiException Refusal(BuildStepRefusal refusal, string name, int current, int last) =>
        refusal switch
        {
            BuildStepRefusal.SingleState => ApiErrors.Refused("no_build_states",
                $"{name} has a single build state, so nothing to build on."),
            BuildStepRefusal.Broken => ApiErrors.Refused("structure_broken",
                $"{name} is in its broken build state; the game cannot build on it."),
            BuildStepRefusal.Complete => ApiErrors.Refused("already_complete",
                $"{name} is complete (build state {current} of {last})."),
            BuildStepRefusal.Damaged => ApiErrors.Refused("structure_damaged",
                $"{name} is damaged; the game builds on it only once it is repaired."),
            _ => ApiErrors.InvalidArgument($"Argument 'to_state' must be above {current} and at most {last}.")
        };

    // from_id's first thing, else the local player: where the materials come from.
    private static Thing SourceOf(List<ThingId> ids)
    {
        ThingId? first = SourceArgs.First(ids);
        if (first.HasValue)
        {
            return GameLookup.RequireThing(first.Value);
        }

        PlayerOrigin player = PlayerOrigin.Current();
        Human? human = player.Player;
        return human != null
            ? human
            : throw ApiErrors.Refused("no_local_player",
                $"{player.Absence} Nothing to take materials from; pass from_id.");
    }
}
