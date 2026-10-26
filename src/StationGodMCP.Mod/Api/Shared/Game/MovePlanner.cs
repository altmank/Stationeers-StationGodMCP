#nullable enable

using System;
using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.Genetics;
using Assets.Scripts.Networking;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Entities;
using Assets.Scripts.Objects.Items;
using Assets.Scripts.Objects.Pipes;
using Objects.Electrical;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Protocol;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>Which slot to move into: a given index, or the first that takes the item.</summary>
internal abstract class SlotChoice
{
    private SlotChoice()
    {
    }

    internal sealed class Exact : SlotChoice
    {
        internal Exact(int index)
        {
            Index = index;
        }

        public int Index { get; }
    }

    internal sealed class Auto : SlotChoice
    {
    }
}

/// <summary>
/// One requested move: the item, how many off its stack (Quantity null for the whole item), the holder and slot it
/// goes to, whether it may join a matching stack (Merge), the cheat that fills a hidden slot as the game's own code
/// does (Force, Pure/ForcedSlotRule), and whether a part its device is using may be taken (AllowInUse,
/// Pure/PartInUseRule).
/// </summary>
internal sealed class ItemMove
{
    internal ItemMove(ThingId item, int? quantity, ThingId target, SlotChoice slot, bool merge, bool force,
        bool allowInUse)
    {
        Item = item;
        Quantity = quantity;
        Target = target;
        Slot = slot;
        Merge = merge;
        Force = force;
        AllowInUse = allowInUse;
    }

    public ThingId Item { get; }

    public int? Quantity { get; }

    public ThingId Target { get; }

    public SlotChoice Slot { get; }

    public bool Merge { get; }

    public bool Force { get; }

    public bool AllowInUse { get; }
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

        refusal = ChooseSlot(move, item!, target!, quantity, out List<MovePart> parts, out Slot? staging);
        if (refusal != null)
        {
            return false;
        }

        bool plants = false;
        foreach (MovePart part in parts)
        {
            GrowerSlotKind kind = GrowerSlots.KindOf(target!, part.Slot);
            plants = item is Plant && kind == GrowerSlotKind.Plant;
            refusal = plants
                ? PlantingRefusal((Plant)item!, target!, part.Slot, part.Quantity)
                : GrowerSlotRefusal(item!, target!, kind, part.Slot, part.Quantity);
            if (refusal != null)
            {
                return false;
            }
        }

        plan = new MovePlan(item!, target!, parts, quantity, staging, plants && parts.Count == 1);
        return true;
    }

    // A plant slot holds one plant, grown from one unit of the stack, as a player plants from the hand.
    private static ApiException? PlantingRefusal(Plant item, Thing target, Slot slot, int quantity)
    {
        if (slot.Get() != null)
        {
            return ApiErrors.Refused("slot_occupied",
                $"{SlotAccess.Label(slot)} already has {Names.Of(slot.Get())} planted; a plant slot holds one "
                + "plant.");
        }

        if (quantity != 1)
        {
            return ApiErrors.InvalidArgument(
                $"Slot {slot.SlotIndex} of {Names.Of(target)} is a plant slot: planting takes one {Names.Of(item)} " +
                "off the stack, as a player plants, so pass quantity 1.");
        }

        return GrowerSlots.PlantingPrefab(item) == null
            ? ApiErrors.Refused("slot_refuses", $"The game has no plant that {Names.Of(item)} grows into.")
            : null;
    }

    // A plant slot takes only what PlantInHand plants; a fertiliser slot is filled as FertiliserInHand fills it: one
    // unit of fertiliser, into an empty slot.
    private static ApiException? GrowerSlotRefusal(DynamicThing item, Thing target, GrowerSlotKind kind, Slot slot,
        int quantity)
    {
        switch (GrowerSlotRule.Into(kind, GrowerSlots.ItemOf(item), slot.Get() != null, quantity))
        {
            case GrowerRefusal.NotPlant:
                return ApiErrors.Refused("slot_refuses",
                    GrowerRefusalText.NotPlant(SlotAccess.Label(slot), item.DisplayName, GrowerSlots.ItemOf(item),
                        GrowerSlots.FertiliserSlotOf(target, slot)?.SlotIndex));
            case GrowerRefusal.NotFertiliser:
                return ApiErrors.Refused("slot_refuses",
                    GrowerRefusalText.NotFertiliser(SlotAccess.Label(slot), GrowerSlots.ItemOf(item)));
            case GrowerRefusal.Occupied:
                return ApiErrors.Refused("slot_occupied",
                    $"{SlotAccess.Label(slot)} already holds {Names.Of(slot.Get())}; a fertiliser slot holds one "
                    + "fertiliser, added only when it is empty.");
            case GrowerRefusal.OneUnit:
                return ApiErrors.InvalidArgument(
                    $"{SlotAccess.Label(slot)} is a fertiliser slot: a player adds one {Names.Of(item)} off the " +
                    "stack, so pass quantity 1.");
            default:
                return null;
        }
    }

    // What the item and the destination are, and how many items move; a refusal when either is unusable.
    private static ApiException? Refusal(ItemMove move, out DynamicThing? item, out Thing? target, out int quantity)
    {
        item = null;
        target = null;
        quantity = 0;
        if (!GameLookup.TryFindThing(move.Item, out Thing found) || found.IsBeingDestroyed)
        {
            // A silo's entries are save data, not things: the refusal names the silo that stores the id, if any.
            return Silos.NotFound(move.Item);
        }

        if (!GameLookup.TryFindThing(move.Target, out Thing destination) || destination.IsBeingDestroyed)
        {
            return ApiErrors.ThingNotFound(move.Target);
        }

        item = found as DynamicThing;
        target = destination;
        if (item == null)
        {
            return ApiErrors.Refused("not_movable", $"{Names.Of(found)} is not an item that fits in a slot.");
        }

        ApiException? refusal = DestinationRefusal(item, destination) ?? SourceRefusal(item, move.AllowInUse);
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
                    "invalid_destination", $"{Names.Of(destination)} is {Names.Of(item)} or inside it.");
            }
        }

        if (destination.Slots == null || destination.Slots.Count == 0)
        {
            return ApiErrors.Refused("no_slots", $"{Names.Of(destination)} has no slots.");
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
                $"Slot {index} of {Names.Of(target)} only shows the vault's store; move the item into the vault " +
                "with no slot to store it.")
            : null;
    }

    // The thing whose slot holds this one, or null when it is not in a slot.
    private static Thing? HolderOf(Thing thing)
    {
        DynamicThing? item = thing as DynamicThing;
        return item != null && item.ParentSlot != null ? item.ParentSlot.Parent : null;
    }

    // A part its device is using (in_use, Pure/PartInUseRule) is judged after the slot's own lock and grower rules.
    private static ApiException? SourceRefusal(DynamicThing item, bool allowInUse)
    {
        Slot? from = item.ParentSlot;
        if (from == null)
        {
            return null;
        }

        if (from.IsLocked)
        {
            return ApiErrors.Refused("slot_locked", $"{Names.Of(item)} is in a locked slot.");
        }

        return GrowerSlotRule.TakesOut(GrowerSlots.KindOf(from.Parent, from), item is Plant, item is Seed)
            ? PartsInUse.Refusal(item, allowInUse)
            : ApiErrors.Refused("planted",
                $"{Names.Of(item)} is growing in {SlotAccess.Label(from)}: a player never takes a plant out whole, " +
                $"only harvests its fruit or seeds once it is mature or seeding (see inspect genes) or " +
                "clears it.");
    }

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
            ? ApiErrors.InvalidArgument($"{Names.Of(item)} has {whole}; cannot take {quantity}.")
            : ApiErrors.InvalidArgument(
                $"{Names.Of(item)} is not a stack: it moves whole, so quantity can only be 1 (or left out).");
    }

    // The slots the move fills (one for a slot given by index), and the empty slot part of a stack is staged in.
    private static ApiException? ChooseSlot(ItemMove move, DynamicThing item, Thing target, int quantity,
        out List<MovePart> parts, out Slot? staging)
    {
        parts = new List<MovePart>(1);
        staging = null;
        switch (move.Slot)
        {
            case SlotChoice.Exact exact:
                ApiException? refused = ExactSlot(move, item, target, quantity, exact.Index, out Slot? slot,
                    out Stackable? mergeInto);
                if (refused == null)
                {
                    parts.Add(new MovePart(slot!, quantity, mergeInto));
                }

                return refused;
            case SlotChoice.Auto _:
                return AutoSlot(move, item, target, quantity, parts, out staging);
            default:
                return ApiErrors.InvalidArgument("Unknown slot.");
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
                "slot_not_found", $"{Names.Of(target)} has no slot {index} (it has {target.Slots.Count}).");
        }

        if (slot == item.ParentSlot)
        {
            return ApiErrors.Refused("same_slot", $"{Names.Of(item)} is already in that slot.");
        }

        ApiException? vaultSlot = VaultSlotRefusal(target, index);
        if (vaultSlot != null)
        {
            return vaultSlot;
        }

        if (slot.IsLocked)
        {
            return ApiErrors.Refused("slot_locked", $"Slot {index} of {Names.Of(target)} is locked.");
        }

        if (GrowerSlotRule.HandDecides(GrowerSlots.KindOf(target, slot)))
        {
            // PlantingRefusal / GrowerSlotRefusal judge it, as the hand interaction does, hidden slot or not.
            return null;
        }

        if (move.Force)
        {
            return ForceRefusal(item, slot, quantity);
        }

        if (!SlotAccess.Reaches(slot))
        {
            return ApiErrors.Refused("slot_refuses",
                SlotAccess.HiddenReason(slot) + (SlotAccess.HeldByStack(slot) ? "" : ForcedSlotRule.HiddenSlotHint));
        }

        return slot.Get() != null
            ? MergeRefusal(move, item, slot, quantity, out mergeInto)
            : EnterRefusal(item, slot);
    }

    // force: Pure/ForcedSlotRule, a whole item into an empty slot whose class takes it, reachable or not.
    private static ApiException? ForceRefusal(DynamicThing item, Slot slot, int quantity)
    {
        int whole = item is Stackable stack ? stack.Quantity : 1;
        switch (SlotAccess.ForcedInto(item, slot, quantity >= whole))
        {
            case ForcedRefusal.StackSlot:
                return ApiErrors.Refused("slot_refuses",
                    SlotAccess.HiddenReason(slot) + " force does not change that.");
            case ForcedRefusal.Occupied:
                return ApiErrors.Refused("slot_occupied",
                    $"{SlotAccess.Label(slot)} holds {Names.Of(slot.Get())}; force fills only an empty slot.");
            case ForcedRefusal.PartOfStack:
                return ApiErrors.InvalidArgument(
                    $"force moves a whole item; leave quantity out (or pass {whole}) for {Names.Of(item)}.");
            case ForcedRefusal.Refuses:
                return ApiErrors.Refused("slot_refuses",
                    $"{SlotAccess.Label(slot)} does not take {Names.Of(item)}: "
                    + $"{SlotAccess.WhyForcedRefused(item, slot)}.");
            default:
                return null;
        }
    }

    // Slot.AllowMove, with the reason it refused (the game's own when Thing.CanEnter says no).
    private static ApiException? EnterRefusal(DynamicThing item, Slot slot) =>
        Slot.AllowMove(item, slot)
            ? null
            : ApiErrors.Refused("slot_refuses",
                $"{SlotAccess.Label(slot)} does not take {Names.Of(item)}: {SlotAccess.WhyRefused(item, slot)}.");

    // Slot.CanMerge, for the whole stack only, and only when all of it fits under Stackable.MaxQuantity
    // (SlotOccupancy.JoinRefusal).
    private static ApiException? MergeRefusal(ItemMove move, DynamicThing item, Slot slot, int quantity,
        out Stackable? mergeInto)
    {
        mergeInto = null;
        SlotOccupant occupant = SlotOccupants.Of(slot, item)!.Value;
        int whole = item is Stackable stack ? stack.Quantity : 1;
        switch (SlotOccupancy.JoinRefusal(occupant, quantity, quantity >= whole, move.Merge))
        {
            case OccupantRefusal.MergeOff:
                return ApiErrors.Refused("slot_occupied",
                    $"{SlotAccess.Label(slot)} holds {Names.Of(slot.Get())}, and merge is false, " +
                    $"so {Names.Of(item)} does not join it.");
            case OccupantRefusal.CannotJoin:
                return ApiErrors.Refused("slot_occupied",
                    $"{SlotAccess.Label(slot)} holds {Names.Of(slot.Get())}, which {Names.Of(item)} cannot join.");
            case OccupantRefusal.PartialMerge:
                return ApiErrors.Refused(
                    "partial_merge", "Part of a stack can only go into an empty slot; the game has no partial merge.");
            case OccupantRefusal.StackFull:
                return ApiErrors.Refused(
                    "stack_full",
                    $"{Names.Of(slot.Get())} holds {occupant.Quantity} of {occupant.MaxQuantity}; "
                    + $"{quantity} more do not fit.");
            default:
                mergeInto = (Stackable)slot.Get();
                return null;
        }
    }

    // Matching stacks topped up first (when merging is allowed), then the first empty slot that takes the item for the
    // rest (SlotOccupancy.FillAuto); any other occupied slot is passed over. A player's slots are those of what it
    // wears, then its hands (Pure/PlayerSlotRule); any other target's are its own. Nothing found: each slot whose
    // class fits says why it did not take the item.
    private static ApiException? AutoSlot(ItemMove move, DynamicThing item, Thing target, int quantity,
        List<MovePart> parts, out Slot? staging)
    {
        List<(Slot Slot, bool Swappable)> slots = target is Human body ? PlayerSlots(body, item) : OwnSlots(target, item);
        List<AutoSlotCandidate> candidates = new List<AutoSlotCandidate>(slots.Count);
        for (int index = 0; index < slots.Count; index++)
        {
            (Slot candidate, bool swappable) = slots[index];
            SlotOccupant? occupant = SlotOccupants.Of(candidate, item);
            bool picks = swappable ? SlotAccess.AutoPicks(candidate) : SlotAccess.Reaches(candidate);
            bool merges = occupant.HasValue && picks && !candidate.IsLocked &&
                          GrowerSlotRule.AutoMerges(GrowerSlots.KindOf(candidate.Parent, candidate));
            bool takesNew = !occupant.HasValue && !candidate.IsLocked && (swappable
                ? AutoTakesNew(item, candidate.Parent, candidate)
                : SlotAccess.TakesNew(item, candidate));
            candidates.Add(new AutoSlotCandidate(index, occupant, merges, takesNew));
        }

        int whole = item is Stackable stack ? stack.Quantity : 1;
        AutoFill fill = SlotOccupancy.FillAuto(candidates, quantity, quantity >= whole, move.Merge);
        foreach (AutoFillPart part in fill.Parts)
        {
            Slot slot = slots[part.Index].Slot;
            parts.Add(new MovePart(slot, part.Quantity, part.Merges ? (Stackable)slot.Get() : null));
        }

        staging = fill.Staging is int staged ? slots[staged].Slot : null;
        return fill.Found
            ? null
            : ApiErrors.Refused("no_free_slot",
                PlayerSlotRule.NoSlot(Names.Of(target), Names.Of(item), move.Merge, Reasons(move, item, quantity, slots)));
    }

    // A target's own slots, less the slot the item is in and a vault's display slots (they only show its store; a
    // stack merged there would go with the slot).
    private static List<(Slot Slot, bool Swappable)> OwnSlots(Thing target, DynamicThing item)
    {
        int usable = IsVaultDisplaySlot(target, VaultDisplaySlotsStart)
            ? System.Math.Min(VaultDisplaySlotsStart, target.Slots.Count)
            : target.Slots.Count;
        List<(Slot Slot, bool Swappable)> slots = new List<(Slot Slot, bool Swappable)>(usable);
        for (int index = 0; index < usable; index++)
        {
            Slot slot = target.Slots[index];
            if (slot != null && slot != item.ParentSlot)
            {
                slots.Add((slot, true));
            }
        }

        return slots;
    }

    // A player's slots as a player puts an item away (Pure/PlayerSlotRule): those of what it wears and of what is
    // stored in them (Refunds.InventorySlots, level by level), then the hands, right first. The item's own slots and
    // the slot it is in are left out.
    private static List<(Slot Slot, bool Swappable)> PlayerSlots(Human body, DynamicThing item)
    {
        List<(Slot Slot, bool Swappable)> slots = new List<(Slot Slot, bool Swappable)>();
        foreach (Slot worn in body.Slots)
        {
            if (worn == null || IsHand(body, worn) || !(worn.Get() is { } wearing) || wearing.IsBeingDestroyed ||
                wearing == item)
            {
                continue;
            }

            foreach (Slot inside in Refunds.InventorySlots(wearing, null))
            {
                if (inside != item.ParentSlot && inside.Parent != item && !IsInside(inside.Parent, item))
                {
                    slots.Add((inside, PlayerSlotRule.NeedsSwappable(PlayerSlotPlace.Worn)));
                }
            }
        }

        foreach (Slot hand in new[] { body.RightHandSlot, body.LeftHandSlot })
        {
            if (hand != null && hand != item.ParentSlot)
            {
                slots.Add((hand, PlayerSlotRule.NeedsSwappable(PlayerSlotPlace.Hand)));
            }
        }

        return slots;
    }

    private static bool IsHand(Human body, Slot slot) =>
        ReferenceEquals(slot, body.LeftHandSlot) || ReferenceEquals(slot, body.RightHandSlot);

    // Whether a thing is held (at any depth) by the item: a move never puts the item inside itself.
    private static bool IsInside(Thing thing, DynamicThing item)
    {
        for (Thing? holder = thing; holder != null; holder = HolderOf(holder))
        {
            if (holder == item)
            {
                return true;
            }
        }

        return false;
    }

    // What each slot whose class fits the item said (a hand takes any class): locked, hidden, skipped, held, refused.
    private static List<string> Reasons(ItemMove move, DynamicThing item, int quantity,
        List<(Slot Slot, bool Swappable)> slots)
    {
        List<string> reasons = new List<string>();
        int whole = item is Stackable stack ? stack.Quantity : 1;
        foreach ((Slot slot, bool swappable) in slots)
        {
            if (slot.Type != Slot.Class.None && slot.Type != item.SlotType)
            {
                continue;
            }

            string label = SlotAccess.Label(slot);
            if (slot.IsLocked)
            {
                reasons.Add($"{label} is locked");
            }
            else if (!SlotAccess.Reaches(slot))
            {
                reasons.Add(SlotAccess.HiddenReason(slot).TrimEnd('.'));
            }
            else if (slot.Get() is { } held)
            {
                SlotOccupant occupant = SlotOccupants.Of(slot, item)!.Value;
                reasons.Add(SlotOccupancy.JoinRefusal(occupant, quantity, quantity >= whole, move.Merge) switch
                {
                    OccupantRefusal.StackFull => $"{label} holds {Names.Of(held)}, {occupant.Quantity} of {occupant.MaxQuantity}",
                    OccupantRefusal.PartialMerge => $"{label} holds {Names.Of(held)} (part of a stack only goes into an empty slot)",
                    OccupantRefusal.MergeOff => $"{label} holds {Names.Of(held)} (merge is false)",
                    _ => $"{label} holds {Names.Of(held)}"
                });
            }
            else if (!Slot.AllowMove(item, slot))
            {
                reasons.Add($"{label} does not take it: {SlotAccess.WhyRefused(item, slot)}");
            }
            else if (swappable && !slot.IsSwappable)
            {
                reasons.Add($"{label} is one the game's quick moves skip: name it with slot");
            }
        }

        return reasons;
    }

    // A grower's plant or fertiliser slot is taken by the grower rules, hidden or not (a plant into a plant slot,
    // fertiliser into a fertiliser slot); any other by the game's quick-move rules.
    private static bool AutoTakesNew(DynamicThing item, Thing target, Slot candidate)
    {
        GrowerSlotKind kind = GrowerSlots.KindOf(target, candidate);
        return GrowerSlotRule.HandDecides(kind)
            ? GrowerSlotRule.AutoTakes(kind, GrowerSlots.ItemOf(item))
            : SlotAccess.AutoTakesNew(item, candidate);
    }
}

/// <summary>One slot a move fills: how many go there, and the stack they join (null: the slot is empty).</summary>
internal sealed class MovePart
{
    internal MovePart(Slot slot, int quantity, Stackable? into)
    {
        Slot = slot;
        Quantity = quantity;
        Into = into;
    }

    internal Slot Slot { get; }

    internal int Quantity { get; }

    internal Stackable? Into { get; }
}

/// <summary>
/// A checked move, applied with the game's calls and read back: into one slot, or (auto) topping up stacks and putting
/// the rest into an empty slot, part of a stack first split into its staging slot.
/// </summary>
internal sealed class MovePlan
{
    internal MovePlan(DynamicThing item, Thing target, List<MovePart> parts, int quantity, Slot? staging, bool plants)
    {
        Item = item;
        Target = target;
        Parts = parts;
        Slot = parts[0].Slot;
        Quantity = quantity;
        MergeInto = parts[0].Into;
        Staging = staging;
        Plants = plants;
    }

    internal DynamicThing Item { get; }

    internal Thing Target { get; }

    internal IReadOnlyList<MovePart> Parts { get; }

    /// <summary>The first slot the move fills.</summary>
    internal Slot Slot { get; }

    internal int Quantity { get; }

    /// <summary>The first stack the move joins; null when its first slot is empty.</summary>
    internal Stackable? MergeInto { get; }

    /// <summary>The empty slot part of a stack is split into before it tops up stacks; null when none is.</summary>
    internal Slot? Staging { get; }

    /// <summary>Whether the slot is a grower's plant slot, so one unit is planted instead of moved.</summary>
    internal bool Plants { get; }

    /// <summary>The slot the item is in now; null when it lies loose in the world.</summary>
    internal SlotRefView? FromSlot => From();

    /// <summary>The stacks the items join; empty when they go into an empty slot only.</summary>
    internal List<ThingId> MergedInto
    {
        get
        {
            List<ThingId> merged = new List<ThingId>(Parts.Count);
            foreach (MovePart part in Parts)
            {
                if (part.Into != null)
                {
                    merged.Add(new ThingId(part.Into.ReferenceId));
                }
            }

            return merged;
        }
    }

    /// <summary>Each slot the move fills and how many go there.</summary>
    internal List<SlotShareView> Shares
    {
        get
        {
            List<SlotShareView> shares = new List<SlotShareView>(Parts.Count);
            foreach (MovePart part in Parts)
            {
                shares.Add(new SlotShareView(new SlotRefView(new ThingId(part.Slot.Parent.ReferenceId), part.Slot.SlotIndex),
                    part.Quantity));
            }

            return shares;
        }
    }

    internal ItemMovedView Apply(int index) => Parts.Count == 1 && Staging == null ? ApplyOne(index) : Fill(index);

    // Several slots: every one checked as it is now, then part of a stack split into its staging slot, each stack
    // topped up from it (or from the moved stack itself), the rest moved or split into its empty slot, and every slot
    // read back.
    private ItemMovedView Fill(int index)
    {
        SlotRefView? from = From();
        ThingId itemId = new ThingId(Item.ReferenceId);
        string name = Item.DisplayName;
        foreach (MovePart part in Parts)
        {
            RequireStillSafe(part.Slot, part.Into, part.Quantity);
        }

        if (Staging != null)
        {
            RequireStillSafe(Staging, null, Quantity);
        }

        Stackable source = (Stackable)Item;
        Stackable feeder = Staging != null ? SplitInto(source, Quantity, Staging) : source;
        List<Stackable> landed = new List<Stackable>(Parts.Count);
        foreach (MovePart part in Parts)
        {
            landed.Add(part.Into != null
                ? TopUp(part.Into, feeder, part.Quantity)
                : Staging != null ? feeder : PutRest(source, part));
        }

        // Read back once every call is made: the stack in each filled slot and what it holds now.
        List<SlotFilledView> filled = new List<SlotFilledView>(Parts.Count);
        int moved = 0;
        for (int at = 0; at < Parts.Count; at++)
        {
            MovePart part = Parts[at];
            DynamicThing? now = part.Slot.Get();
            moved += part.Quantity;
            filled.Add(new SlotFilledView(new SlotRefView(new ThingId(part.Slot.Parent.ReferenceId), part.Slot.SlotIndex),
                new ThingId(landed[at].ReferenceId), part.Quantity, part.Into != null,
                now is Stackable stack && stack == landed[at] ? stack.Quantity : (int?)null));
        }

        return new ItemMovedView(index, itemId, name, from, filled[0].To, moved,
            filled[0].Merged ? filled[0].ReferenceId : (ThingId?)null, filled[0].ReferenceId, filled: filled);
    }

    private void RequireStillSafe(Slot slot, Stackable? into, int quantity)
    {
        if (!SlotOccupancy.StillSafe(SlotOccupants.Of(slot, Item),
                into != null ? into.ReferenceId : (long?)null, into != null ? quantity : 0))
        {
            DynamicThing? now = slot.Get();
            throw ApiErrors.Refused(now != null ? "slot_occupied" : "move_failed",
                $"{SlotAccess.Label(slot)} now holds {(now != null ? Names.Of(now) : "nothing")}, not what the move "
                + "was checked against; nothing was moved (a move never replaces what a slot holds).");
        }
    }

    // Stackable.Merge moves all of feeder that fits; the plan asked exactly that many of it.
    private Stackable TopUp(Stackable into, Stackable feeder, int quantity)
    {
        int before = into.Quantity;
        OnServer.Merge(into, feeder);
        return into.Quantity == before + quantity ? into : throw Failed(slot: into.ParentSlot);
    }

    // The rest of a whole stack into its empty slot: the stack itself.
    private Stackable PutRest(Stackable source, MovePart part)
    {
        OnServer.MoveToSlot(source, part.Slot);
        return part.Slot.Get() == source && source.Quantity == part.Quantity ? source : throw Failed(slot: part.Slot);
    }

    private Stackable SplitInto(Stackable stack, int quantity, Slot slot)
    {
        int before = stack.Quantity;
        Stackable made = stack.SplitStack(quantity, slot);
        if (made != null && slot.Get() == made && stack.Quantity == before - quantity)
        {
            return made;
        }

        PutBack(stack, made, before);
        throw Failed(slot: slot);
    }

    private ItemMovedView ApplyOne(int index)
    {
        SlotRefView? from = From();
        SlotRefView to = To();
        ThingId itemId = new ThingId(Item.ReferenceId);
        string name = Item.DisplayName;
        ThingId? merged = Merged();
        int mergedBefore = MergeInto != null ? MergeInto.Quantity : 0;
        if (!SlotOccupancy.StillSafe(SlotOccupants.Of(Slot, Item), merged?.Value, Quantity))
        {
            DynamicThing? now = Slot.Get();
            throw ApiErrors.Refused(now != null ? "slot_occupied" : "move_failed",
                $"{SlotAccess.Label(Slot)} now holds {(now != null ? Names.Of(now) : "nothing")}, not what the move "
                + "was checked against; nothing was moved (a move never replaces what a slot holds).");
        }

        try
        {
            DynamicThing landed = Perform();
            return new ItemMovedView(index, itemId, name, from, to, Quantity, merged, new ThingId(landed.ReferenceId));
        }
        catch (Exception thrown) when (!(thrown is ApiException))
        {
            DynamicThing? landed = LandedAfter(mergedBefore);
            if (landed == null)
            {
                throw Failed(thrown);
            }

            int moved = MergeInto != null
                ? MergeInto.Quantity - mergedBefore
                : landed is Stackable stack ? stack.Quantity : 1;
            return new ItemMovedView(index, itemId, name, from, to, moved, merged, new ThingId(landed.ReferenceId),
                $"The game threw during the move ({thrown.GetType().Name}: {thrown.Message}), but the slot holds the " +
                "result, so the move is done; check it with inspect contents.");
        }
    }

    private SlotRefView? From() => Item.ParentSlot == null
        ? null
        : new SlotRefView(new ThingId(Item.ParentSlot.Parent.ReferenceId), Item.ParentSlot.SlotIndex);

    private SlotRefView To() => new SlotRefView(new ThingId(Slot.Parent.ReferenceId), Slot.SlotIndex);

    private ThingId? Merged() => MergeInto != null ? new ThingId(MergeInto.ReferenceId) : (ThingId?)null;

    private DynamicThing Perform() =>
        MergeInto != null ? Merge(MergeInto) : Plants ? PlantOne() : IsSplit() ? Split() : MoveWhole();

    // What the move left in the slot after a game call threw: the stack it joined once that grew; otherwise whatever
    // is in the slot, which was empty before (only a merge goes into an occupied slot).
    private DynamicThing? LandedAfter(int mergedBefore) =>
        MergeInto != null ? (MergeInto.Quantity > mergedBefore ? MergeInto : null) : Slot.Get();

    private bool IsSplit() => Item is Stackable stack && Quantity < stack.Quantity;

    private DynamicThing MoveWhole()
    {
        OnServer.MoveToSlot(Item, Slot);
        return Slot.Get() == Item ? Item : throw Failed();
    }

    private DynamicThing Split() => SplitInto((Stackable)Item, Quantity, Slot);

    // Stackable.SplitStack takes the part off the source before it makes the new stack; a stack the slot then refuses
    // is left in the world at the origin (OnServer.Create: MoveToSlotOrWorld), and one the game did not make is gone.
    // Either way the part goes back onto the source, so a failed split loses nothing.
    private static void PutBack(Stackable source, Stackable? made, int before)
    {
        if (made != null && !made.IsBeingDestroyed && made.ParentSlot == null)
        {
            OnServer.Merge(source, made);
        }
        else if (made == null)
        {
            source.AddQuantity(before - source.Quantity);
        }
    }

    // HydroponicsUtils.PlantInHand: the genes of the unit used, one unit off the stack, a new plant made in the slot.
    // The genes go on in finally, so a plant the game made before throwing still gets them.
    private DynamicThing PlantOne()
    {
        Plant seed = (Plant)Item;
        GeneCollection genes = GeneCollection.Copy(seed.Genes);
        Thing prefab = GrowerSlots.PlantingPrefab(seed)!;
        if (!seed.OnUseItem(1f, Target))
        {
            throw Failed();
        }

        try
        {
            OnServer.Create<Plant>(prefab, Slot);
        }
        finally
        {
            if (Slot.Get() is Plant grown)
            {
                grown.ApplySeedTraits(genes);
            }
        }

        return Slot.Get() is Plant planted ? planted : throw Failed();
    }

    private DynamicThing Merge(Stackable into)
    {
        int before = into.Quantity;
        OnServer.Merge(into, (Stackable)Item);
        return into.Quantity == before + Quantity ? into : throw Failed();
    }

    private ApiException Failed(Exception? thrown = null, Slot? slot = null) =>
        ApiErrors.Refused(
            "move_failed",
            $"The game did not put {Names.Of(Item)} into slot {(slot ?? Slot).SlotIndex} of {Names.Of((slot ?? Slot).Parent)}"
            + (thrown == null ? "" : $" (it threw {thrown.GetType().Name}: {thrown.Message})")
            + "; read the slots before retrying.");
}

/// <summary>What a slot holds, for SlotOccupancy: null when empty.</summary>
internal static class SlotOccupants
{
    internal static SlotOccupant? Of(Slot slot, DynamicThing moving)
    {
        DynamicThing? occupant = slot.Get();
        if (occupant == null)
        {
            return null;
        }

        return occupant is Stackable stack
            ? new SlotOccupant(stack.ReferenceId, stack.Quantity, stack.MaxQuantity,
                moving is Stackable && Slot.CanMerge(moving, slot))
            : new SlotOccupant(occupant.ReferenceId, 1, 1, false);
    }
}

/// <summary>
/// A grower's plant and fertiliser slots: hydroponics trays, stations, portable and automated hydroponics.
/// </summary>
internal static class GrowerSlots
{
    // IGrower.PlantToFertiliserSlotMapping maps the planting interactable of a plant slot (Slot.Action) to that slot
    // and its fertiliser slot, and throws for any other interactable.
    internal static GrowerSlotKind KindOf(Thing holder, Slot slot)
    {
        if (!(holder is IGrower grower) || holder.Slots == null)
        {
            return GrowerSlotKind.Other;
        }

        foreach (Slot candidate in holder.Slots)
        {
            if (candidate == null || !TryMapping(grower, candidate, out PlantToFertiliserSlotMapping mapping))
            {
                continue;
            }

            if (mapping.PlantSlot == slot)
            {
                return GrowerSlotKind.Plant;
            }

            if (mapping.FertiliserSlot == slot)
            {
                return GrowerSlotKind.Fertiliser;
            }
        }

        return GrowerSlotKind.Other;
    }

    /// <summary>The fertiliser slot mapped to this plant slot, or null when there is none.</summary>
    internal static Slot? FertiliserSlotOf(Thing holder, Slot plantSlot)
    {
        if (!(holder is IGrower grower) || holder.Slots == null)
        {
            return null;
        }

        foreach (Slot candidate in holder.Slots)
        {
            if (candidate != null && TryMapping(grower, candidate, out PlantToFertiliserSlotMapping mapping) &&
                mapping.PlantSlot == plantSlot)
            {
                return mapping.FertiliserSlot;
            }
        }

        return null;
    }

    /// <summary>What the item is to a grower's hand: a plant (seed bags included), a fertiliser, or neither.</summary>
    internal static GrowerItem ItemOf(DynamicThing item) =>
        item is Plant ? GrowerItem.Plant : item is Fertiliser ? GrowerItem.Fertiliser : GrowerItem.Other;

    private static bool TryMapping(IGrower grower, Slot slot, out PlantToFertiliserSlotMapping mapping)
    {
        try
        {
            mapping = grower.PlantToFertiliserSlotMapping(slot.Action);
            return true;
        }
        catch (Exception)
        {
            mapping = default;
            return false;
        }
    }

    /// <summary>What planting it grows: a seed's Seed.PlantType, a plant's own prefab; null when unresolved.</summary>
    internal static Thing? PlantingPrefab(Plant item) => item is Seed seed ? seed.PlantType : item.SourcePrefab;
}
