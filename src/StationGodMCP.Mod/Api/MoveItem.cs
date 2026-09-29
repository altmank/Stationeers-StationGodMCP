#nullable enable

using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.Networking;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Items;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;

namespace StationGodMCP.Api;

/// <summary>
/// move_item: move an item, or part of a stack, from wherever it is into a slot, with the game's own moves, so it never
/// passes through the world. Writes; host only.
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
/// </summary>
internal static class MoveItemApi
{
    internal const int MaximumMoves = 64;

    internal static object Handle(Args args)
    {
        if (NetworkManager.IsClient || !GameManager.RunSimulation)
        {
            throw ApiErrors.Refused("not_host", "This game is a multiplayer client; only the host moves items.");
        }

        switch (MoveItemRequest.Parse(args))
        {
            case MoveItemRequest.Single single:
                return MoveOrThrow(single.Move);
            case MoveItemRequest.Batch batch:
                return MoveEach(batch.Moves);
            default:
                throw ApiErrors.InvalidArgument("Unknown move_item form.");
        }
    }

    private static ItemMovedView MoveOrThrow(ItemMove move)
    {
        if (!MovePlanner.TryPlan(move, out MovePlan? plan, out ApiException? refusal))
        {
            throw refusal!;
        }

        return plan!.Apply(0);
    }

    private static BatchResultView MoveEach(List<ParsedMove> moves)
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

            MoveOne(batch, index, plan!);
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
            return new Single(ItemMove.Parse(args));
        }

        args.Reject("moves", "reference_id", "quantity", "to_id", "to_slot", "merge");
        JArray list = args.Array("moves", MoveItemApi.MaximumMoves);
        List<ParsedMove> moves = new List<ParsedMove>(list.Count);
        for (int index = 0; index < list.Count; index++)
        {
            moves.Add(ParsedMove.From(list[index], index));
        }

        return new Batch(moves);
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
            return new ParsedMove(ItemMove.Parse(new Args(entry)), item, null);
        }
        catch (ApiException error)
        {
            // Args refuses a malformed argument of this entry; the other entries still run.
            return new ParsedMove(null, item, error);
        }
    }
}

/// <summary>Which slot to move into: a given index, or the first that takes the item.</summary>
internal abstract class SlotChoice
{
    private const string AutoWord = "auto";

    private SlotChoice()
    {
    }

    internal static SlotChoice Parse(Args args)
    {
        if (args.IsWord("to_slot", AutoWord))
        {
            return new Auto();
        }

        int? index = args.OptionalInt("to_slot", 0, int.MaxValue);
        return index.HasValue
            ? new Exact(index.Value)
            : throw ApiErrors.InvalidArgument("Argument 'to_slot' is required: a slot index, or \"auto\".");
    }

    internal sealed class Exact : SlotChoice
    {
        internal Exact(int index)
        {
            Index = index;
        }

        internal int Index { get; }
    }

    internal sealed class Auto : SlotChoice
    {
    }
}

/// <summary>One requested move, as parsed.</summary>
internal sealed class ItemMove
{
    private ItemMove(ThingId item, int? quantity, ThingId target, SlotChoice slot, bool merge)
    {
        Item = item;
        Quantity = quantity;
        Target = target;
        Slot = slot;
        Merge = merge;
    }

    internal ThingId Item { get; }

    /// <summary>How many to take off the stack; null for the whole item.</summary>
    internal int? Quantity { get; }

    internal ThingId Target { get; }

    internal SlotChoice Slot { get; }

    /// <summary>Whether the items may join a matching stack already in the slot.</summary>
    internal bool Merge { get; }

    internal static ItemMove Parse(Args args) =>
        new ItemMove(
            args.ThingId("reference_id"),
            args.OptionalInt("quantity", 1, int.MaxValue),
            args.ThingId("to_id"),
            SlotChoice.Parse(args),
            args.OptionalBool("merge") ?? true);
}

/// <summary>Checks one move against the game's rules and settles which slot and which stack it uses.</summary>
internal static class MovePlanner
{
    internal static bool TryPlan(ItemMove move, out MovePlan? plan, out ApiException? refusal)
    {
        plan = null;
        refusal = Refusal(move, out DynamicThing? item, out Thing? target, out int quantity);
        if (refusal != null)
        {
            return false;
        }

        refusal = ChooseSlot(move, item!, target!, quantity, out Slot? slot, out Stackable? mergeInto);
        if (refusal != null)
        {
            return false;
        }

        plan = new MovePlan(item!, target!, slot!, quantity, mergeInto);
        return true;
    }

    // What the item and the destination are, and how many items move; a refusal when either is unusable.
    private static ApiException? Refusal(ItemMove move, out DynamicThing? item, out Thing? target, out int quantity)
    {
        item = null;
        target = null;
        quantity = 0;
        if (!GameLookup.TryFindThing(move.Item, out Thing found) || found.IsBeingDestroyed)
        {
            return ApiErrors.ThingNotFound(move.Item);
        }

        if (!GameLookup.TryFindThing(move.Target, out Thing destination) || destination.IsBeingDestroyed)
        {
            return ApiErrors.ThingNotFound(move.Target);
        }

        item = found as DynamicThing;
        target = destination;
        if (item == null)
        {
            return ApiErrors.Refused("not_movable", $"{found.DisplayName} is not an item that fits in a slot.");
        }

        ApiException? refusal = DestinationRefusal(item, destination) ?? SourceRefusal(item);
        quantity = 0;
        return refusal ?? QuantityRefusal(move, item, out quantity);
    }

    private static ApiException? DestinationRefusal(DynamicThing item, Thing destination)
    {
        for (Thing? holder = destination; holder != null; holder = HolderOf(holder))
        {
            if (holder == item)
            {
                return ApiErrors.Refused(
                    "invalid_destination", $"{destination.DisplayName} is {item.DisplayName} or inside it.");
            }
        }

        if (destination.Slots == null || destination.Slots.Count == 0)
        {
            return ApiErrors.Refused("no_slots", $"{destination.DisplayName} has no slots.");
        }

        return null;
    }

    // An Ingot Vault or Remote Vault keeps slots past its import and export slots only to show its store (IngotVault's
    // UpdateSlots adds and removes them); an item put there would be dropped with the slot.
    internal static bool IsVaultDisplaySlot(Thing target, int index) =>
        index >= VaultDisplaySlotsStart && (IngotVaults.IsVault(target) || IngotVaults.IsRemote(target));

    private const int VaultDisplaySlotsStart = 2;

    private static ApiException? VaultSlotRefusal(Thing target, int index)
    {
        return IsVaultDisplaySlot(target, index)
            ? ApiErrors.Refused("vault_display_slot",
                $"Slot {index} of {target.DisplayName} only shows the vault's store; use vault_deposit to store items.")
            : null;
    }

    // The thing whose slot holds this one, or null when it is not in a slot.
    private static Thing? HolderOf(Thing thing)
    {
        DynamicThing? item = thing as DynamicThing;
        return item != null && item.ParentSlot != null ? item.ParentSlot.Parent : null;
    }

    private static ApiException? SourceRefusal(DynamicThing item) =>
        item.ParentSlot != null && item.ParentSlot.IsLocked
            ? ApiErrors.Refused("slot_locked", $"{item.DisplayName} is in a locked slot.")
            : null;

    // quantity counts a stack's items; anything else (a water packet, a canister) moves whole, as 1.
    private static ApiException? QuantityRefusal(ItemMove move, DynamicThing item, out int quantity)
    {
        int whole = item is Stackable stack ? stack.Quantity : 1;
        quantity = move.Quantity ?? whole;
        if (quantity <= whole)
        {
            return null;
        }

        return item is Stackable
            ? ApiErrors.InvalidArgument($"{item.DisplayName} has {whole}; cannot take {quantity}.")
            : ApiErrors.InvalidArgument(
                $"{item.DisplayName} is not a stack: it moves whole, so quantity can only be 1 (or left out).");
    }

    private static ApiException? ChooseSlot(ItemMove move, DynamicThing item, Thing target, int quantity,
        out Slot? slot, out Stackable? mergeInto)
    {
        slot = null;
        mergeInto = null;
        switch (move.Slot)
        {
            case SlotChoice.Exact exact:
                return ExactSlot(move, item, target, quantity, exact.Index, out slot, out mergeInto);
            case SlotChoice.Auto _:
                return AutoSlot(move, item, target, quantity, out slot, out mergeInto);
            default:
                return ApiErrors.InvalidArgument("Unknown to_slot.");
        }
    }

    private static ApiException? ExactSlot(ItemMove move, DynamicThing item, Thing target, int quantity, int index,
        out Slot? slot, out Stackable? mergeInto)
    {
        mergeInto = null;
        slot = index < target.Slots.Count ? target.Slots[index] : null;
        if (slot == null)
        {
            return ApiErrors.Refused(
                "slot_not_found", $"{target.DisplayName} has no slot {index} (it has {target.Slots.Count}).");
        }

        if (slot == item.ParentSlot)
        {
            return ApiErrors.Refused("same_slot", $"{item.DisplayName} is already in that slot.");
        }

        ApiException? vaultSlot = VaultSlotRefusal(target, index);
        if (vaultSlot != null)
        {
            return vaultSlot;
        }

        if (slot.IsLocked)
        {
            return ApiErrors.Refused("slot_locked", $"Slot {index} of {target.DisplayName} is locked.");
        }

        if (!SlotAccess.Reaches(slot))
        {
            return ApiErrors.Refused("slot_refuses", SlotAccess.HiddenReason(slot));
        }

        return slot.Get() != null
            ? MergeRefusal(move, item, slot, quantity, out mergeInto)
            : EnterRefusal(item, slot);
    }

    // Slot.AllowMove, with the reason it refused (the game's own when Thing.CanEnter says no).
    private static ApiException? EnterRefusal(DynamicThing item, Slot slot) =>
        Slot.AllowMove(item, slot)
            ? null
            : ApiErrors.Refused("slot_refuses",
                $"{SlotAccess.Label(slot)} does not take {item.DisplayName}: {SlotAccess.WhyRefused(item, slot)}.");

    // Slot.CanMerge, for the whole stack only, and only when all of it fits under Stackable.MaxQuantity.
    private static ApiException? MergeRefusal(ItemMove move, DynamicThing item, Slot slot, int quantity,
        out Stackable? mergeInto)
    {
        mergeInto = null;
        Stackable? occupant = slot.Get() as Stackable;
        if (!move.Merge || occupant == null || !(item is Stackable stack) || !Slot.CanMerge(item, slot))
        {
            return ApiErrors.Refused("slot_occupied",
                $"{SlotAccess.Label(slot)} holds {slot.Get().DisplayName}, which {item.DisplayName} cannot join.");
        }

        if (quantity < stack.Quantity)
        {
            return ApiErrors.Refused(
                "partial_merge", "Part of a stack can only go into an empty slot; the game has no partial merge.");
        }

        if (occupant.Quantity + quantity > occupant.MaxQuantity)
        {
            return ApiErrors.Refused(
                "stack_full",
                $"{occupant.DisplayName} holds {occupant.Quantity} of {occupant.MaxQuantity}; "
                + $"{quantity} more do not fit.");
        }

        mergeInto = occupant;
        return null;
    }

    // A matching stack first (when merging is allowed and the whole stack moves), then the first empty slot that
    // takes the item.
    private static ApiException? AutoSlot(ItemMove move, DynamicThing item, Thing target, int quantity,
        out Slot? slot, out Stackable? mergeInto)
    {
        for (int index = 0; index < target.Slots.Count && move.Merge; index++)
        {
            Slot candidate = target.Slots[index];
            if (candidate != null && candidate != item.ParentSlot && candidate.Get() != null &&
                SlotAccess.AutoPicks(candidate) && MergeRefusal(move, item, candidate, quantity, out mergeInto) == null)
            {
                slot = candidate;
                return null;
            }
        }

        mergeInto = null;
        int usable = IsVaultDisplaySlot(target, VaultDisplaySlotsStart)
            ? System.Math.Min(VaultDisplaySlotsStart, target.Slots.Count)
            : target.Slots.Count;
        for (int index = 0; index < usable; index++)
        {
            Slot candidate = target.Slots[index];
            if (candidate != null && candidate.Get() == null && SlotAccess.AutoTakesNew(item, candidate))
            {
                slot = candidate;
                return null;
            }
        }

        slot = null;
        return ApiErrors.Refused("no_free_slot", $"{target.DisplayName} has no slot that takes {item.DisplayName}.");
    }
}

/// <summary>A checked move, applied with the game's calls and read back.</summary>
internal sealed class MovePlan
{
    internal MovePlan(DynamicThing item, Thing target, Slot slot, int quantity, Stackable? mergeInto)
    {
        Item = item;
        Target = target;
        Slot = slot;
        Quantity = quantity;
        MergeInto = mergeInto;
    }

    internal DynamicThing Item { get; }

    internal Thing Target { get; }

    internal Slot Slot { get; }

    internal int Quantity { get; }

    internal Stackable? MergeInto { get; }

    internal ItemMovedView Apply(int index)
    {
        SlotRefView? from = Item.ParentSlot == null
            ? null
            : new SlotRefView(new ThingId(Item.ParentSlot.Parent.ReferenceId), Item.ParentSlot.SlotIndex);
        SlotRefView to = new SlotRefView(new ThingId(Target.ReferenceId), Slot.SlotIndex);
        ThingId itemId = new ThingId(Item.ReferenceId);
        DynamicThing landed = MergeInto != null ? Merge(MergeInto) : IsSplit() ? Split() : MoveWhole();
        ThingId? merged = MergeInto != null ? new ThingId(MergeInto.ReferenceId) : (ThingId?)null;
        return new ItemMovedView(index, itemId, from, to, Quantity, merged, new ThingId(landed.ReferenceId));
    }

    private bool IsSplit() => Item is Stackable stack && Quantity < stack.Quantity;

    private DynamicThing MoveWhole()
    {
        OnServer.MoveToSlot(Item, Slot);
        return Slot.Get() == Item ? Item : throw Failed();
    }

    private DynamicThing Split()
    {
        Stackable stack = (Stackable)Item;
        int before = stack.Quantity;
        Stackable made = stack.SplitStack(Quantity, Slot);
        if (made == null || Slot.Get() != made || stack.Quantity != before - Quantity)
        {
            throw Failed();
        }

        return made;
    }

    private DynamicThing Merge(Stackable into)
    {
        int before = into.Quantity;
        OnServer.Merge(into, (Stackable)Item);
        return into.Quantity == before + Quantity ? into : throw Failed();
    }

    private ApiException Failed() =>
        ApiErrors.Refused(
            "move_failed",
            $"The game did not put {Item.DisplayName} into slot {Slot.SlotIndex} of {Target.DisplayName}; "
            + "read the slots before retrying.");
}
