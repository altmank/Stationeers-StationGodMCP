#nullable enable

using System;
using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.Networking;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Chutes;
using Assets.Scripts.Objects.Items;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Protocol;
using UnityEngine;

namespace StationGodMCP.Api;

/// <summary>
/// silo_withdraw: take items of one prefab out of an SDB Silo's store straight into a holder's slots, with the silo's
/// own bookkeeping, as a move: what the store loses is what the holder gains. No export slot or door is involved, so
/// nothing is dropped in front of the silo. Writes; host only.
///
/// Entries are taken front to back, the silo's export order (Silo.BeginNextExport, Silo.cs:211). A stack entry holding
/// nothing that does not spoil is pooled: the amount is made as items into the holder as vault_withdraw makes them
/// (Delivery: matching stacks topped up first, then new full stacks into empty slots), and the store goes down entry
/// by entry, the last one keeping the rest (its StackableSaveData or ConsumableSaveData Quantity). Any other entry (a
/// thing that is not a stack, holds things, or is food) is loaded whole, contents and all, into an empty slot as the
/// silo's export loads it (SiloLoader), food rotten as the export makes it. After the store changes the silo refreshes
/// as after its own export (SiloStore.Replace). What a failed game call did not make stays in the store.
/// </summary>
internal static class SiloWithdrawApi
{
    internal static SiloWithdrawView Handle(Args args)
    {
        if (NetworkManager.IsClient || !GameManager.RunSimulation)
        {
            throw ApiErrors.Refused("not_host", "This game is a multiplayer client; only the host moves items.");
        }

        SiloWithdrawRequest request = SiloWithdrawRequest.Of(args, WriteMode.IsDryRun(args));
        SiloStore store = SiloStore.Of(Silos.Resolve(request.Silo));
        List<SiloEntry> entries = store.Describe();
        SiloPick.Picked pick = SiloPicks.Choose(store, entries, request.Prefab, request.Quantity);
        SiloEntry first = entries[pick.FirstIndex];
        DynamicThing? template = first.Template;
        if (template == null)
        {
            throw ApiErrors.Refused("prefab_missing",
                $"This game has no prefab {first.PrefabName} to make the stored thing from.");
        }

        if (template is Stackable && Math.Abs(request.Quantity - Math.Round(request.Quantity)) > SiloPick.Trace)
        {
            throw ApiErrors.InvalidArgument(
                $"{Names.Of(template)} counts whole items; quantity {request.Quantity} is not whole.");
        }

        Thing holder = Holder(request);
        store.RequirePowered();
        RequireDispenseKept(store, first);
        Delivery delivery = Delivery.Survey(holder,
            request.ToSlot.HasValue ? new SlotChoice.Exact(request.ToSlot.Value) : new SlotChoice.Auto(), template);
        SiloPlacement placement = SiloPlacement.Plan(pick.Pooled,
            template is Item item ? Delivery.FullStack(item) : 1.0, delivery.Rooms, delivery.Empty.Count, pick.Whole,
            request.AllowGround);
        if (!placement.Fits)
        {
            throw ApiErrors.Refused("no_room",
                $"{Names.Of(holder)} has no room for all {request.Quantity} {Names.Of(template)} " +
                $"({placement.Pooled.Unplaced + placement.Whole.Unplaced} left over; a thing stored with contents or " +
                "a stored food takes an empty slot of its own). Free a slot, name another holder, or pass " +
                $"{Vocabulary.Arg("allow_ground")}: true for the rest. Nothing was changed.");
        }

        double before = SiloPicks.Total(entries, request.Prefab);
        int countBefore = store.Entries.Count;
        SiloWithdrawal withdrawal = new SiloWithdrawal(store, entries, pick, delivery, placement, holder);
        List<PlacedView> placed = request.DryRun ? withdrawal.Preview() : withdrawal.Run(template);
        SiloStore after = request.DryRun ? store : SiloStore.Of(store.Silo);
        double stockAfter = request.DryRun
            ? before - request.Quantity
            : SiloPicks.Total(after.Describe(), request.Prefab);
        int countAfter = request.DryRun ? countBefore - SiloPicks.Leaving(pick) : after.Entries.Count;
        return new SiloWithdrawView(request.DryRun, store.View(), GameLookup.ViewOf(holder), request.Quantity,
            withdrawal.Taken(), placed,
            new SiloStockView(first.PrefabName, first.DisplayName, before, -request.Quantity, stockAfter),
            new SiloCountView(countBefore, countAfter));
    }

    // DispenseSlot is an index into the store: taking that entry, or one before it, would make the IC's export pick
    // another entry. The silo clears it on its next export (BeginNextExport) or when it points past the end.
    private static void RequireDispenseKept(SiloStore store, SiloEntry first)
    {
        int dispense = store.DispenseSlot;
        if (SiloRules.ShiftsDispenseSlot(dispense, first.Index))
        {
            throw ApiErrors.Refused("silo_busy",
                $"An IC asked {Names.Of(store.Silo)} to dispense entry {dispense} (DispenseSlot), and this would take " +
                $"entry {first.Index}, at or before it, so the dispense would get another entry. Let the silo export " +
                "it first (it does when on, powered and its export is free). Nothing was changed.");
        }
    }

    // to_id, else the player; never a silo's import or export slot, which would import or export it again.
    private static Thing Holder(SiloWithdrawRequest request)
    {
        Thing holder = request.To.HasValue ? GameLookup.RequireThing(request.To.Value) : PlayerOrigin.RequireHuman();
        if (holder.IsBeingDestroyed)
        {
            throw ApiErrors.ThingNotFound(new ThingId(holder.ReferenceId));
        }

        if (holder is Silo || IngotVaults.IsVault(holder) || IngotVaults.IsRemote(holder))
        {
            throw ApiErrors.Refused("invalid_destination",
                "A silo's or vault's slots are its import and export slots; name the holder that should get the items.");
        }

        return holder.Slots != null && holder.Slots.Count > 0
            ? holder
            : throw ApiErrors.Refused("no_slots", $"{Names.Of(holder)} has no slots.");
    }
}
