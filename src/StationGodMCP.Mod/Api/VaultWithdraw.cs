#nullable enable

using System;
using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.Networking;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Entities;
using Assets.Scripts.Objects.Items;
using Objects.Items;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using UnityEngine;

namespace StationGodMCP.Api;

/// <summary>
/// vault_withdraw: take an amount of one stored thing out of an Ingot Vault's store and make it straight into a
/// holder's slots, as a move: what the store loses is what the holder gains. No vend queue, export slot or door is
/// involved, so nothing is dropped in front of the vault. Writes; host only; needs IngotVault.
///
/// The vault's own vend (CODE, IngotVault 1.2.0): TryVendSelected takes min(stock, stack size) off the store (an
/// ingot's reagent Set to what is left; an ore entry removed when 0.01 or less is left, NetworkUpdateFlags 0x100) and
/// queues it; OnServerTick makes the item (the ingot GetPrefabHashForReagent names, or the ore's prefab) with
/// Quantity = the amount, at most the prefab's MaxQuantity per item, into the Export slot. This takes the store down the
/// same way (VaultStock.Take) and makes the items with OnServer.Create into the chosen slots, as a fabricator's output
/// is made, topping up matching stacks first (Stackable.AddQuantity; Consumable.Quantity, as the Stacker's Combine
/// does for ingots). The ground is used only with allow_ground.
/// </summary>
internal static class VaultWithdrawApi
{
    internal static VaultWithdrawView Handle(Args args)
    {
        if (NetworkManager.IsClient || !GameManager.RunSimulation)
        {
            throw ApiErrors.Refused("not_host", "This game is a multiplayer client; only the host moves items.");
        }

        IngotVaults.RequireLoaded();
        bool dryRun = WriteMode.IsDryRun(args);
        VaultTarget target = VaultTarget.Resolve(args.ThingId("vault_id"));
        VaultStore store = target.Store();
        VaultStock stock = WithdrawStock.Find(store, args);
        double quantity = args.OptionalPositiveDouble("quantity") ??
                          throw ApiErrors.InvalidArgument("Argument 'quantity' is required.");
        Item? prefab = stock.ItemPrefab;
        if (prefab == null)
        {
            throw ApiErrors.Refused("prefab_missing", $"No item prefab {stock.PrefabHash} exists to make.");
        }

        RequireAmount(store, stock, prefab, quantity);
        Thing holder = Holder(args);
        target.RequirePowered();
        Delivery delivery = Delivery.Survey(holder, Choice(args), prefab);
        StackPlan plan = StackPlacement.Plan(quantity, Delivery.FullStack(prefab), delivery.Rooms,
            delivery.Empty.Count, args.OptionalBool("allow_ground") ?? false);
        if (!plan.Fits)
        {
            throw ApiErrors.Refused("no_room",
                $"{holder.DisplayName} takes {VaultAmount.Of(plan.Placed)} of {quantity} {prefab.DisplayName}; " +
                "free a slot, name another holder, or pass allow_ground: true for the rest. Nothing was changed.");
        }

        VaultLedger ledger = new VaultLedger(store);
        ledger.Record(stock, -quantity);
        List<PlacedView> placed = dryRun
            ? delivery.Preview(plan)
            : delivery.Deliver(plan, store, stock, quantity);
        return new VaultWithdrawView(dryRun, target.View(), GameLookup.ViewOf(holder), quantity, placed,
            ledger.Views(readBack: !dryRun));
    }

    private static void RequireAmount(VaultStore store, VaultStock stock, Item prefab, double quantity)
    {
        double amount = stock.AmountIn(store);
        if (quantity > amount + VaultStore.Trace)
        {
            throw ApiErrors.Refused("not_enough_stock",
                $"The vault holds {VaultAmount.Of(amount)} {prefab.DisplayName}; cannot take {quantity}.");
        }

        if (stock.CountsWhole && Math.Abs(quantity - Math.Round(quantity)) > VaultStore.Trace)
        {
            throw ApiErrors.InvalidArgument($"{prefab.DisplayName} counts whole items; quantity {quantity} is not whole.");
        }
    }

    // to_id, else the local player.
    private static Thing Holder(Args args)
    {
        ThingId? id = args.OptionalThingId("to_id");
        Thing holder = id.HasValue ? GameLookup.RequireThing(id.Value) : PlayerOrigin.RequireHuman();
        if (holder.IsBeingDestroyed)
        {
            throw ApiErrors.ThingNotFound(new ThingId(holder.ReferenceId));
        }

        if (IngotVaults.IsVault(holder) || IngotVaults.IsRemote(holder))
        {
            throw ApiErrors.Refused("invalid_destination",
                "A vault's slots are its import, export and display slots; name the holder that should get the items.");
        }

        return holder.Slots != null && holder.Slots.Count > 0
            ? holder
            : throw ApiErrors.Refused("no_slots", $"{holder.DisplayName} has no slots.");
    }

    private static SlotChoice Choice(Args args) => args.Has("to_slot") ? SlotChoice.Parse(args) : new SlotChoice.Auto();
}

/// <summary>Which stored thing a withdrawal names: prefab_name, prefab_hash, or reagent (ingots only).</summary>
internal static class WithdrawStock
{
    internal static VaultStock Find(VaultStore store, Args args)
    {
        string? prefabName = args.OptionalString("prefab_name");
        int? prefabHash = args.OptionalInt("prefab_hash", int.MinValue, int.MaxValue);
        string? reagent = args.OptionalString("reagent");
        int named = (prefabName != null ? 1 : 0) + (prefabHash.HasValue ? 1 : 0) + (reagent != null ? 1 : 0);
        if (named != 1)
        {
            throw ApiErrors.InvalidArgument("Name what to take with exactly one of prefab_name, prefab_hash or reagent.");
        }

        List<VaultStock> lines = store.Lines();
        foreach (VaultStock line in lines)
        {
            if (Matches(line, prefabName, prefabHash, reagent))
            {
                return line;
            }
        }

        throw ApiErrors.Refused("not_in_vault",
            $"The vault holds no {prefabName ?? reagent ?? prefabHash.ToString()}; it holds {Names(lines)}.");
    }

    private static bool Matches(VaultStock line, string? prefabName, int? prefabHash, string? reagent)
    {
        if (prefabHash.HasValue)
        {
            return line.PrefabHash == prefabHash.Value;
        }

        if (prefabName != null)
        {
            Item? prefab = line.ItemPrefab;
            return prefab != null && string.Equals(prefab.PrefabName, prefabName, StringComparison.OrdinalIgnoreCase);
        }

        return line is VaultStock.IngotStock ingot &&
               (string.Equals(ingot.Reagent.TypeName, reagent, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(ingot.Reagent.DisplayName, reagent, StringComparison.OrdinalIgnoreCase));
    }

    private static string Names(List<VaultStock> lines)
    {
        if (lines.Count == 0)
        {
            return "nothing";
        }

        List<string> names = new List<string>(lines.Count);
        foreach (VaultStock line in lines)
        {
            names.Add(line.View().PrefabName ?? line.Key);
        }

        return string.Join(", ", names);
    }
}

/// <summary>
/// The slots a withdrawal can use in its holder: matching stacks with room, and empty slots that take the item
/// (Slot.AllowMove). to_slot auto on a player: every slot the player carries at any depth (belts, backpack, suit), new
/// stacks never into the player's own body slots, a stack already in a hand topped up (as Refunds does); on anything
/// else its own slots. An index names one slot.
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

    internal static Delivery Survey(Thing holder, SlotChoice choice, Item prefab)
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

    private void OfferExact(int index, Item prefab)
    {
        Slot? slot = index < Holder.Slots.Count ? Holder.Slots[index] : null;
        if (slot == null)
        {
            throw ApiErrors.Refused("slot_not_found",
                $"{Holder.DisplayName} has no slot {index} (it has {Holder.Slots.Count}).");
        }

        if (slot.IsLocked)
        {
            throw ApiErrors.Refused("slot_locked", $"Slot {index} of {Holder.DisplayName} is locked.");
        }

        Offer(slot, prefab, bodySlots: true);
        if (Stacks.Count == 0 && Empty.Count == 0)
        {
            DynamicThing? occupant = slot.Get();
            throw occupant != null
                ? ApiErrors.Refused("slot_occupied",
                    $"{slot.DisplayName} holds {occupant.DisplayName}, which {prefab.DisplayName} cannot join.")
                : ApiErrors.Refused("slot_refuses", $"{slot.DisplayName} does not take {prefab.DisplayName}.");
        }
    }

    // bodySlots: whether a new stack may go into the player's own body slots (only when named by index).
    private void Offer(Slot slot, Item prefab, bool bodySlots)
    {
        if (slot == null || slot.IsLocked)
        {
            return;
        }

        DynamicThing? occupant = slot.Get();
        if (occupant == null)
        {
            if ((bodySlots || slot.Parent != Body) && Slot.AllowMove(prefab, slot))
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
    private static double RoomOn(DynamicThing occupant, Item prefab) =>
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

    /// <summary>Takes the store down, then makes every step; what could not be made goes back into the store.</summary>
    internal List<PlacedView> Deliver(StackPlan plan, VaultStore store, VaultStock stock, double quantity)
    {
        List<PlacedView> placed = new List<PlacedView>(plan.Steps.Count);
        double delivered = 0.0;
        stock.Take(store, quantity);
        try
        {
            foreach (PlacementStep step in plan.Steps)
            {
                Make(step, stock.ItemPrefab!, placed);
                delivered += step.Quantity;
            }
        }
        catch (Exception)
        {
            // A game call failed part way: the store keeps what was not made, so nothing is lost; the reply is the
            // error (ApiHost), and vault_contents shows the result.
            stock.Add(store, quantity - delivered);
            store.Refresh();
            throw;
        }

        store.Refresh();
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

    private Vector3 Ground() => Refunds.GroundBeside(Holder, Body != null ? Body : Holder.RootParentHuman);

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

    private static SlotRefView RefOf(Slot slot) => new SlotRefView(new ThingId(slot.Parent.ReferenceId), slot.SlotIndex);
}
