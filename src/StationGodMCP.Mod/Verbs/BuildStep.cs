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
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>
/// build finish's work on one structure, as a player's construction does it (Structure.AttackWith): the state it
/// reaches, what the states it adds take (BuildStepRule.Cost) and the tools they use (BuildStates.Needs), the materials
/// taken from the holders in order (PaySources) with materials_from_player said when the player pays, and, when the call
/// acts, the take and the new state (CurrentBuildStateIndex, whose setter updates the cell's air, the build-state sounds
/// and the network flag clients sync by, then UpdateStateVisualizer). Everything the structure itself is refused for
/// comes back as that id's failure, so a list carries on with the rest; a step that is refused or short of materials
/// leaves the structure and the holders as they were.
/// </summary>
internal static class BuildStep
{
    /// <summary>
    /// One id's step: the structure read and checked, the materials found and, when acts, taken. The id as the call
    /// gave it is read first (looking_at, a ref the hub expanded), and every refusal is that id's own: acting already
    /// happened only when the step came back ready.
    /// </summary>
    internal static FinishResult Plan(FinishOrder order, int index, string given, bool acts)
    {
        ThingId id;
        try
        {
            id = TargetIds.Of(given, order.Label);
        }
        catch (ApiException refused)
        {
            return new FinishResult.Failed(index, given, VerbError.Of(refused));
        }

        try
        {
            return Step(order, index, id, acts);
        }
        catch (ApiException refused)
        {
            return new FinishResult.Failed(index, id.ToString(), VerbError.Of(refused));
        }
    }

    // The whole rehearsal of one structure, and the act itself: nothing before the take changes anything.
    private static FinishResult Step(FinishOrder order, int index, ThingId id, bool acts)
    {
        if (NetworkManager.IsClient || !GameManager.RunSimulation)
        {
            throw ApiErrors.Refused("not_host", "This game is a multiplayer client; only the host builds.");
        }

        Thing thing = GameLookup.RequireThing(id);
        if (!(thing is Structure structure) || structure.BuildStates == null)
        {
            throw ApiErrors.Refused("no_build_states", $"{Names.Of(thing)} is not a structure with build states.");
        }

        int current = structure.CurrentBuildStateIndex;
        int last = structure.BuildStates.Count - 1;
        int target = order.Target(current);
        bool damaged = structure.DamageState != null && structure.DamageState.Total > 0f;
        if (FinishRefusals.Of(BuildStepRule.Refusal(current, structure.BuildStates.Count, target, damaged),
                $"{Names.Of(structure)} {structure.ReferenceId}", current, last, order.Label) is { } refusal)
        {
            return new FinishResult.Failed(index, id.ToString(), refusal);
        }

        bool free = order.Free;
        if (PlacementRule.FreeRefusal(free, WorldManager.IsCreative()) is { } freeRefusal)
        {
            throw ApiErrors.Refused("not_creative", $"free: {freeRefusal}.");
        }

        List<ThingId> more = new List<ThingId>();
        ThingId? payer = free ? null : JobTargets.Payers(order.Materials, order.Label, more);
        Thing? from = free ? null : Source(order, payer);
        List<ThingId> missing = new List<ThingId>();
        List<Thing> others = free ? new List<Thing>() : PaySources.Resolve(more, missing);
        if (missing.Count > 0)
        {
            throw ApiErrors.ThingNotFound(missing[0]);
        }

        Dictionary<int, Item> items = new Dictionary<int, Item>();
        List<IReadOnlyList<BuildEntry>> states = BuildMaterials.EntriesOf(structure, items);
        List<ItemStock> stocks = new List<ItemStock>();
        List<BuildCostView> cost = new List<BuildCostView>();
        List<string> shortages = new List<string>();
        foreach (ItemCount count in BuildStepRule.Cost(states, current, target))
        {
            Item item = items[count.Item];
            ItemStock stock = free ? ItemStock.Empty(item) : PaySources.StockOf(from, others, item);
            stock.Needed = count.Quantity;
            stocks.Add(stock);
            cost.Add(new BuildCostView(item.PrefabName, Names.Of(item), count.Quantity,
                free ? (int?)null : stock.Available, PaySources.PaidBy(stock)));
            if (!free && stock.Available < count.Quantity)
            {
                shortages.Add($"{count.Quantity} {Names.Of(item)} ({item.PrefabName}) needed, {stock.Available} held " +
                              $"by {PaySources.Holders(from!, others)}.");
            }
        }

        List<BuildNeedView> tools = BuildStates.Needs(structure, current, target).FindAll(static need => need.Tool);
        List<BuildIssueView> warnings = new List<BuildIssueView>();
        GuardFinding? fromPlayer = PaySources.PlayerPays(!free && stocks.Exists(static stock => stock.Needed > 0),
            from, others, payer.HasValue);
        if (fromPlayer != null)
        {
            warnings.Add(new BuildIssueView(fromPlayer.Code, fromPlayer.Message));
        }

        FinishStep step = new FinishStep(index, id, GameLookup.ViewOf(structure),
            from != null ? GameLookup.ViewOf(from) : null, Names.Of(structure), current, target, last, cost, tools,
            warnings,
            shortages.Count > 0 ? new VerbError("not_enough_materials", string.Join(" ", shortages)) : null);
        if (acts && step.Ready)
        {
            foreach (ItemStock stock in stocks)
            {
                stock.Take(stock.Needed);
            }

            structure.CurrentBuildStateIndex = target;
            structure.UpdateStateVisualizer();
            if (BuildStates.Of(structure) is { } state)
            {
                step.Stepped(state);
            }
        }

        return new FinishResult.Stepped(step);
    }

    // Who pays: the holders materials names, else the local player (no_local_player when the world has none).
    private static Thing Source(FinishOrder order, ThingId? payer)
    {
        if (payer.HasValue)
        {
            return GameLookup.RequireThing(payer.Value);
        }

        PlayerOrigin player = PlayerOrigin.Current();
        Human? human = player.Player;
        return human != null
            ? human
            : throw ApiErrors.Refused("no_local_player",
                $"{player.Absence} Nothing to take materials from; name a holder in materials.");
    }
}
