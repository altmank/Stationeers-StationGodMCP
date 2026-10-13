#nullable enable

using System.Collections.Generic;
using System;
using Assets.Scripts.Networking;
using Assets.Scripts.Objects.Entities;
using Assets.Scripts.Objects.Items;
using Assets.Scripts.Objects;
using Assets.Scripts;
using Objects.Items;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure.Protocol;
using StationGodMCP.Pure;
using UnityEngine;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// The slots a withdrawal can use in its holder: matching stacks with room, and empty slots that take the item
/// (Slot.AllowMove), never a hidden slot (SlotAccess: not interactable, such as a cable coil's). to_slot auto on a
/// player: every slot the player carries at any depth (belts, backpack, suit), new stacks never into the player's own
/// body slots, a stack already in a hand topped up (as Refunds does); on anything else its own slots. An index names
/// one slot.
/// </summary>
internal sealed class Delivery
{
    private Delivery(Thing holder, Human? body, List<Slot> stacks, List<double> rooms, List<Slot> empty)
    {
        Holder = holder;
        Body = body;
        Stacks = stacks;
        Rooms = rooms;
        Empty = empty;
    }

    private Thing Holder { get; }

    private Human? Body { get; }

    private List<Slot> Stacks { get; }

    internal List<double> Rooms { get; }

    internal List<Slot> Empty { get; }

    internal static double FullStack(Item prefab) =>
        prefab switch
        {
            Stackable stackable when stackable.MaxQuantity > 0 => stackable.MaxQuantity,
            Consumable consumable when consumable.MaxQuantity > 0f => consumable.MaxQuantity,
            _ => 1.0
        };

    internal static Delivery Survey(Thing holder, SlotChoice choice, DynamicThing prefab)
    {
        Human? body = holder as Human;
        Delivery delivery = new Delivery(holder, body, new List<Slot>(), new List<double>(), new List<Slot>());
        switch (choice)
        {
            case SlotChoice.Exact exact:
                delivery.OfferExact(exact.Index, prefab);
                break;
            default:
                foreach (Slot slot in body != null ? Refunds.InventorySlots(body, body) : new List<Slot>(holder.Slots))
                {
                    delivery.Offer(slot, prefab, bodySlots: false);
                }

                break;
        }

        return delivery;
    }

    private void OfferExact(int index, DynamicThing prefab)
    {
        Slot? slot = index < Holder.Slots.Count ? Holder.Slots[index] : null;
        if (slot == null)
        {
            throw ApiErrors.Refused("slot_not_found",
                $"{Names.Of(Holder)} has no slot {index} (it has {Holder.Slots.Count}).");
        }

        if (slot.IsLocked)
        {
            throw ApiErrors.Refused("slot_locked", $"Slot {index} of {Names.Of(Holder)} is locked.");
        }

        if (!SlotAccess.Reaches(slot))
        {
            throw ApiErrors.Refused("slot_refuses", SlotAccess.HiddenReason(slot));
        }

        Offer(slot, prefab, bodySlots: true);
        if (Stacks.Count == 0 && Empty.Count == 0)
        {
            DynamicThing? occupant = slot.Get();
            throw occupant != null
                ? ApiErrors.Refused("slot_occupied",
                    $"{SlotAccess.Label(slot)} holds {Names.Of(occupant)}, which {Names.Of(prefab)} cannot join.")
                : ApiErrors.Refused("slot_refuses",
                    $"{SlotAccess.Label(slot)} does not take {Names.Of(prefab)}: {SlotAccess.WhyRefused(prefab, slot)}.");
        }
    }

    // bodySlots: whether a new stack may go into the player's own body slots (only when named by index, which is
    // also the only case where a slot the game's quick moves skip may be used).
    private void Offer(Slot slot, DynamicThing prefab, bool bodySlots)
    {
        if (slot == null || slot.IsLocked || !SlotAccess.Reaches(slot))
        {
            return;
        }

        DynamicThing? occupant = slot.Get();
        if (occupant == null)
        {
            if (bodySlots ? SlotAccess.TakesNew(prefab, slot)
                    : slot.Parent != Body && SlotAccess.AutoTakesNew(prefab, slot))
            {
                Empty.Add(slot);
            }

            return;
        }

        double room = occupant.IsBeingDestroyed ? 0.0 : RoomOn(occupant, prefab);
        if (room > VaultStore.Trace)
        {
            Stacks.Add(slot);
            Rooms.Add(room);
        }
    }

    // Stackable.CanStack for ores; the same prefab for ingots (the Stacker's rule for Consumable.Combine).
    private static double RoomOn(DynamicThing occupant, DynamicThing prefab) =>
        (occupant, prefab) switch
        {
            (Stackable stack, Stackable kind) when stack.CanStack(kind) => stack.MaxQuantity - stack.Quantity,
            (Consumable held, Consumable _) when held.PrefabHash == prefab.PrefabHash => held.MaxQuantity - held.Quantity,
            _ => 0.0
        };

    internal List<PlacedView> Preview(StackPlan plan)
    {
        List<PlacedView> placed = new List<PlacedView>(plan.Steps.Count);
        foreach (PlacementStep step in plan.Steps)
        {
            placed.Add(step switch
            {
                PlacementStep.TopUp topUp => new PlacedView(PlacedView.Merged, RefOf(Stacks[topUp.Stack]),
                    step.Quantity, new ThingId(Stacks[topUp.Stack].Get().ReferenceId)),
                PlacementStep.NewStack newStack => new PlacedView(PlacedView.InSlot, RefOf(Empty[newStack.Slot]),
                    step.Quantity, null),
                _ => new PlacedView(PlacedView.Ground, null, step.Quantity, null)
            });
        }

        return placed;
    }

    /// <summary>Takes the source down, then makes every step; what could not be made goes back to the source.</summary>
    internal List<PlacedView> Deliver(StackPlan plan, Item prefab, IWithdrawalSource source, double quantity)
    {
        List<PlacedView> placed = new List<PlacedView>(plan.Steps.Count);
        double delivered = 0.0;
        source.Take(quantity);
        try
        {
            foreach (PlacementStep step in plan.Steps)
            {
                Make(step, prefab, placed);
                delivered += step.Quantity;
            }
        }
        catch (Exception)
        {
            // A game call failed part way: the source keeps what was not made, so nothing is lost; the reply is the
            // error (ApiHost), and the source's own read tool shows the result.
            source.Return(quantity - delivered);
            source.Settle();
            throw;
        }

        source.Settle();
        return placed;
    }

    private void Make(PlacementStep step, Item prefab, List<PlacedView> placed)
    {
        switch (step)
        {
            case PlacementStep.TopUp topUp:
                TopUp(Stacks[topUp.Stack], prefab, step.Quantity, placed);
                break;
            case PlacementStep.NewStack newStack:
                Slot slot = Empty[newStack.Slot];
                Item made = OnServer.Create<Item>(prefab, slot);
                SetAmount(made, step.Quantity);
                if (made.ParentSlot == null)
                {
                    // The slot refused it after all and the game left it in the world: bring it down beside the holder.
                    OnServer.MoveToWorld(made, Ground(), Quaternion.identity);
                }

                placed.Add(made.ParentSlot != null
                    ? new PlacedView(PlacedView.InSlot, RefOf(slot), step.Quantity, new ThingId(made.ReferenceId))
                    : new PlacedView(PlacedView.Ground, null, step.Quantity, new ThingId(made.ReferenceId)));
                break;
            default:
                placed.Add(new PlacedView(PlacedView.Ground, null, step.Quantity,
                    new ThingId(MakeAtGround(prefab, step.Quantity).ReferenceId)));
                break;
        }
    }

    private void TopUp(Slot slot, Item prefab, double quantity, List<PlacedView> placed)
    {
        DynamicThing onto = slot.Get();
        double added = quantity;
        if (onto is Stackable stack)
        {
            added = stack.AddQuantity((int)Math.Round(quantity));
        }
        else
        {
            ((Consumable)onto).Quantity += (float)quantity;
        }

        placed.Add(new PlacedView(PlacedView.Merged, RefOf(slot), added, new ThingId(onto.ReferenceId)));
        if (added < quantity - VaultStore.Trace)
        {
            // The stack filled between the survey and the merge; the rest goes down beside the holder.
            placed.Add(new PlacedView(PlacedView.Ground, null, quantity - added,
                new ThingId(MakeAtGround(prefab, quantity - added).ReferenceId)));
        }
    }

    private Item MakeAtGround(Item prefab, double quantity)
    {
        Item item = OnServer.Create<Item>(prefab, Ground(), Quaternion.identity);
        SetAmount(item, quantity);
        return item;
    }

    internal Vector3 Ground() => Refunds.GroundBeside(Holder, Body != null ? Body : Holder.RootParentHuman);

    // As the vend sets a new item's amount: an ingot's grams, an ore stack's count.
    private static void SetAmount(Item item, double quantity)
    {
        switch (item)
        {
            case Stackable stack:
                stack.SetQuantity((int)Math.Round(quantity));
                break;
            case Consumable consumable:
                consumable.Quantity = (float)quantity;
                break;
        }
    }

    internal static SlotRefView RefOf(Slot slot) => new SlotRefView(new ThingId(slot.Parent.ReferenceId), slot.SlotIndex);
}

/// <summary>Where a withdrawal's amount comes from: taken before the items are made, the part not made given back.</summary>
internal interface IWithdrawalSource
{
    void Take(double quantity);

    void Return(double quantity);

    /// <summary>After the last change: whatever the source refreshes once its own take or put is done.</summary>
    void Settle();
}

/// <summary>An Ingot Vault's stock line as a withdrawal's source: the vend's take, the import's add, the display refresh.</summary>
internal sealed class VaultSource : IWithdrawalSource
{
    private readonly VaultStore _store;
    private readonly VaultStock _stock;

    internal VaultSource(VaultStore store, VaultStock stock)
    {
        _store = store;
        _stock = stock;
    }

    public void Take(double quantity) => _stock.Take(_store, quantity);

    public void Return(double quantity) => _stock.Add(_store, quantity);

    public void Settle() => _store.Refresh();
}
