#nullable enable

using System;
using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.Genetics;
using Assets.Scripts.Networking;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Items;
using Assets.Scripts.Objects.Pipes;
using Newtonsoft.Json.Linq;
using Objects.Electrical;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Protocol;

namespace StationGodMCP.Api;

/// <summary>
/// move_item: move an item, or part of a stack, from wherever it is into a slot, with the game's own moves, so it never
/// passes through the world. Writes; host only. dry_run reports the moves from the same checks and moves nothing
/// (ReportOnly).
///
/// The moves (CODE): a whole item goes with OnServer.MoveToSlot, which calls DynamicThing.MoveToSlot. That empties the
/// old slot and tells its parent (Thing.OnChildExitInventory, which is how a VendingMachine keeps its CurrentSlot and
/// filled-slot count right), then fills the new slot (Slot.Take, Thing.OnChildEnterInventory). Part of a stack goes
/// with Stackable.SplitStack(quantity, slot), as the Stacker does: the source keeps the rest and a new stack is made
/// straight into the slot (OnServer.Create into a slot). A whole stack joins a matching one with OnServer.Merge, i.e.
/// Stackable.Merge (Ore carries QuantitySmelted, Plant its genes).
///
/// The checks are the game's: the slot must be one a player can reach (Pure/SlotReach: Slot.IsInteractable, as
/// Thing.HandleSwitch asks; a hidden slot, such as a cable coil's, destroys what is put there with its holder);
/// Slot.AllowMove (the slot is not locked, is empty, its class is None or the item's SlotType, the item's
/// Thing.CanEnter allows it, and it is not a draggable) for an empty slot; Slot.CanMerge (Stackable.CanStack: same
/// prefab) for an occupied one, and room for the whole stack under Stackable.MaxQuantity. "auto" also skips a slot
/// that is not Slot.IsSwappable, as the game's quick moves do. A locked source slot is refused, as Slot.AllowSwap does;
/// an item may be taken out of a hidden slot (that is how one put there by mistake is rescued). A part of a stack
/// cannot join another stack: the game has no call that merges part of one without first making a new stack in an
/// empty slot.
/// An item its device is using (Pure/PartInUseRule: a chip in a chip holder, a console's motherboard, and while the
/// device is switched on its filter, battery or canister) is refused as in_use unless allow_in_use.
/// The slot is read again right before the game call (SlotOccupancy.StillSafe): a move never replaces, drops or
/// destroys what a slot holds.
///
/// Planting (CODE, HydroponicsUtils.PlantInHand, as a player plants from the hand): a seed or plant into a grower's
/// plant slot (IGrower.PlantToFertiliserSlotMapping) is not moved there. One unit is used off the stack
/// (Stackable.OnUseItem), a new plant is made in the slot from the seed's Seed.PlantType (a plant's own prefab
/// otherwise) and takes the used unit's genes (Plant.ApplySeedTraits). Moving the seed itself leaves a seed bag in the
/// tray with no growth stages, whose Plant.RefreshVisualizers throws.
///
/// A grower's plant and fertiliser slots are judged by its hand interactions (Pure/GrowerSlotRule), which never ask
/// whether the slot is interactable: a planter's slots and a station's fertiliser slots are hidden in the inventory
/// window, yet a player plants and fertilises them by hand. Its plant slot takes only a seed or plant (fertiliser in
/// the hand goes to the fertiliser slot, anything else is refused), its fertiliser slot one unit of fertiliser into
/// an empty slot, nothing else (most growers take any plant entering any slot for their plant); auto puts each kind
/// only into its own slot, and a plant growing in a plant slot is never taken out whole: a player only harvests or
/// clears it.
///
/// force (a cheat, single move only): a whole item into an empty slot with an index, hidden or not, by
/// Pure/ForcedSlotRule: the rest of Slot.AllowMove without the reach and draggable rules, as RocketPayload.AttackWith
/// mounts a payload in a payload bay's hidden slot with OnServer.MoveToSlot. A grower's slots keep their hand rules.
///
/// A game call that throws part way is read back: when the slot holds the result, the move is reported done with the
/// game's error as a warning, never as an internal error, so a client does not repeat a move that happened.
/// </summary>
internal static class MoveItemApi
{
    internal const int MaximumMoves = 64;

    internal static object Handle(Args args)
    {
        bool report = ReportOnly.Asked(args);
        MoveItemRequest request = MoveItemRequest.Parse(args);
        return WriteCheck<CheckedMoves>.Run(() => Check(request)).Answer(report, Move, Describe);
    }

    // One move is checked here, so its refusal refuses the call; a list's moves are each checked right before their
    // own move (MoveEach), after the moves before them.
    private static CheckedMoves Check(MoveItemRequest request)
    {
        if (NetworkManager.IsClient || !GameManager.RunSimulation)
        {
            throw ApiErrors.Refused("not_host", "This game is a multiplayer client; only the host moves items.");
        }

        switch (request)
        {
            case MoveItemRequest.Single single:
                return MovePlanner.TryPlan(single.Move, out MovePlan? plan, out ApiException? refusal)
                    ? new CheckedMoves(plan!, null)
                    : throw refusal!;
            case MoveItemRequest.Batch batch:
                return new CheckedMoves(null, batch.Moves);
            default:
                throw ApiErrors.InvalidArgument(Vocabulary.Text("Unknown {tool:move_item} form."));
        }
    }

    private static object Move(CheckedMoves moves) =>
        moves.One != null ? moves.One.Apply(0) : MoveEach(moves.List!, report: false);

    private static object Describe(CheckedMoves moves) =>
        moves.One != null
            ? new MoveReportView(moves.One.Planned(0))
            : new MovesReportView(MoveEach(moves.List!, report: true));

    // report checks each move against the world as it is, without the moves before it, and moves nothing.
    private static BatchResultView MoveEach(List<ParsedMove> moves, bool report)
    {
        BatchBuilder batch = new BatchBuilder(moves.Count);
        for (int index = 0; index < moves.Count; index++)
        {
            ParsedMove parsed = moves[index];
            if (parsed.Move == null)
            {
                batch.Failed(new ItemNotMovedView(index, parsed.Item, parsed.Error!));
                continue;
            }

            if (!MovePlanner.TryPlan(parsed.Move, out MovePlan? plan, out ApiException? refusal))
            {
                batch.Failed(new ItemNotMovedView(index, parsed.Move.Item, refusal!));
                continue;
            }

            if (report)
            {
                batch.Succeeded(plan!.Planned(index));
            }
            else
            {
                MoveOne(batch, index, plan!);
            }
        }

        return batch.Build();
    }

    private static void MoveOne(BatchBuilder batch, int index, MovePlan plan)
    {
        try
        {
            batch.Succeeded(plan.Apply(index));
        }
        catch (ApiException refusal)
        {
            // MovePlan.Apply reads the result back and throws move_failed when the game did not do the move.
            batch.Failed(new ItemNotMovedView(index, new ThingId(plan.Item.ReferenceId), refusal));
        }
    }

    /// <summary>A checked call: one move's plan, or a list whose moves are checked one by one.</summary>
    private sealed class CheckedMoves
    {
        internal CheckedMoves(MovePlan? one, List<ParsedMove>? list)
        {
            One = one;
            List = list;
        }

        internal MovePlan? One { get; }

        internal List<ParsedMove>? List { get; }
    }
}

/// <summary>The two forms of move_item: one move, or a list applied in order.</summary>
internal abstract class MoveItemRequest
{
    private MoveItemRequest()
    {
    }

    internal static MoveItemRequest Parse(Args args)
    {
        if (!args.Has("moves"))
        {
            return new Single(MoveOf(args, args.OptionalBool("force") ?? false));
        }

        args.Reject("moves", "reference_id", "quantity", "to_id", "to_slot", "merge", "force", "allow_in_use");
        JArray list = args.Array("moves", MoveItemApi.MaximumMoves);
        List<ParsedMove> moves = new List<ParsedMove>(list.Count);
        for (int index = 0; index < list.Count; index++)
        {
            moves.Add(ParsedMove.From(list[index], index));
        }

        return new Batch(moves);
    }

    /// <summary>One move's arguments: reference_id, quantity, to_id, to_slot, merge and allow_in_use.</summary>
    internal static ItemMove MoveOf(Args args, bool force)
    {
        SlotChoice slot = SlotOf(args);
        if (force && slot is SlotChoice.Auto)
        {
            throw ApiErrors.InvalidArgument(Vocabulary.Text("{arg:force} needs {arg:to_slot} as a slot index, not " +
                "\"auto\"."));
        }

        return new ItemMove(
            args.ThingId("reference_id"),
            args.OptionalInt("quantity", 1, int.MaxValue),
            args.ThingId("to_id"),
            slot,
            args.OptionalBool("merge") ?? true,
            force,
            args.OptionalBool("allow_in_use") ?? false);
    }

    /// <summary>to_slot: a slot index, or "auto".</summary>
    internal static SlotChoice SlotOf(Args args)
    {
        if (args.IsWord("to_slot", "auto"))
        {
            return new SlotChoice.Auto();
        }

        int? index = args.OptionalInt("to_slot", 0, int.MaxValue);
        return index.HasValue
            ? new SlotChoice.Exact(index.Value)
            : throw ApiErrors.InvalidArgument(Vocabulary.Text("Argument '{arg:to_slot}' is required: a slot index, " +
                "or \"auto\"."));
    }

    internal sealed class Single : MoveItemRequest
    {
        internal Single(ItemMove move)
        {
            Move = move;
        }

        internal ItemMove Move { get; }
    }

    internal sealed class Batch : MoveItemRequest
    {
        internal Batch(List<ParsedMove> moves)
        {
            Moves = moves;
        }

        internal List<ParsedMove> Moves { get; }
    }
}

/// <summary>One entry of a batch: the move, or why it could not be read.</summary>
internal sealed class ParsedMove
{
    private ParsedMove(ItemMove? move, ThingId? item, ApiException? error)
    {
        Move = move;
        Item = item;
        Error = error;
    }

    internal ItemMove? Move { get; }

    /// <summary>The entry's reference_id when it could be read, for the refusal.</summary>
    internal ThingId? Item { get; }

    internal ApiException? Error { get; }

    internal static ParsedMove From(JToken token, int index)
    {
        if (!(token is JObject entry))
        {
            return new ParsedMove(null, null, ApiErrors.InvalidArgument($"moves[{index}] must be an object."));
        }

        ThingId? item = ThingId.TryRead(entry["reference_id"], out ThingId read) ? read : (ThingId?)null;
        try
        {
            return new ParsedMove(MoveItemRequest.MoveOf(new Args(entry), force: false), item, null);
        }
        catch (ApiException error)
        {
            // Args refuses a malformed argument of this entry; the other entries still run.
            return new ParsedMove(null, item, error);
        }
    }
}
