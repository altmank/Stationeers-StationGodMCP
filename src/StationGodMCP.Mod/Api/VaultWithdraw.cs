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
using StationGodMCP.Pure.Protocol;
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
        VaultStock stock = StockOf(store, args);
        double quantity = args.OptionalPositiveDouble("quantity") ??
                          throw ApiErrors.InvalidArgument(Vocabulary.Text("Argument '{arg:quantity}' is required."));
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
                $"{Names.Of(holder)} takes {VaultAmount.Of(plan.Placed)} of {quantity} {Names.Of(prefab)}; free a " +
                $"slot, name another holder, or pass {Vocabulary.Arg("allow_ground")}: true for the rest. Nothing " +
                "was changed.");
        }

        VaultLedger ledger = new VaultLedger(store);
        ledger.Record(stock, -quantity);
        List<PlacedView> placed = dryRun
            ? delivery.Preview(plan)
            : delivery.Deliver(plan, prefab, new VaultSource(store, stock), quantity);
        return new VaultWithdrawView(dryRun, target.View(), GameLookup.ViewOf(holder), quantity, placed,
            ledger.Views(readBack: !dryRun));
    }

    private static void RequireAmount(VaultStore store, VaultStock stock, Item prefab, double quantity)
    {
        double amount = stock.AmountIn(store);
        if (quantity > amount + VaultStore.Trace)
        {
            throw ApiErrors.Refused("not_enough_stock",
                $"The vault holds {VaultAmount.Of(amount)} {Names.Of(prefab)}; cannot take {quantity}.");
        }

        if (stock.CountsWhole && Math.Abs(quantity - Math.Round(quantity)) > VaultStore.Trace)
        {
            throw ApiErrors.InvalidArgument($"{Names.Of(prefab)} counts whole items; quantity {quantity} is not whole.");
        }
    }

    // to_id, else the player.
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
            : throw ApiErrors.Refused("no_slots", $"{Names.Of(holder)} has no slots.");
    }

    // Exactly one of prefab_name, prefab_hash or reagent.
    private static VaultStock StockOf(VaultStore store, Args args)
    {
        string? prefabName = args.OptionalString("prefab_name");
        int? prefabHash = args.OptionalInt("prefab_hash", int.MinValue, int.MaxValue);
        string? reagent = args.OptionalString("reagent");
        int named = (prefabName != null ? 1 : 0) + (prefabHash.HasValue ? 1 : 0) + (reagent != null ? 1 : 0);
        if (named != 1)
        {
            throw ApiErrors.InvalidArgument(Vocabulary.Text("Name what to take with exactly one of " +
                "{arg:prefab_name}, {arg:prefab_hash} or {arg:reagent}."));
        }

        return WithdrawStock.Find(store, prefabName, prefabHash, reagent);
    }

    private static SlotChoice Choice(Args args) => args.Has("to_slot") ? MoveItemRequest.SlotOf(args) : new SlotChoice.Auto();
}
