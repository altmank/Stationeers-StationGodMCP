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
        SiloPick.Picked pick = SiloPicks.Choose(store, entries, request);
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
                "allow_ground: true for the rest. Nothing was changed.");
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

/// <summary>Which entries a request takes, with the silo's own words when it cannot.</summary>
internal static class SiloPicks
{
    private const int NamesListed = 12;

    internal static SiloPick.Picked Choose(SiloStore store, List<SiloEntry> entries, SiloWithdrawRequest request)
    {
        List<SiloEntryFacts> facts = new List<SiloEntryFacts>(entries.Count);
        foreach (SiloEntry entry in entries)
        {
            facts.Add(new SiloEntryFacts(request.Prefab.Matches(entry.PrefabName, entry.PrefabHash), entry.Quantity,
                entry.Pooled));
        }

        string silo = Names.Of(store.Silo);
        switch (SiloPick.Choose(facts, request.Quantity))
        {
            case SiloPick.Picked picked:
                return picked;
            case SiloPick.NotEnough short_:
                throw ApiErrors.Refused("not_enough_stock",
                    $"{silo} holds {VaultAmount.Of(short_.Available)} {request.Prefab.Asked}; cannot take " +
                    $"{request.Quantity}.");
            case SiloPick.SplitsEntry split:
                SiloEntry entry = entries[split.Index];
                string ask = split.Below > SiloPick.Trace
                    ? $"ask for {VaultAmount.Of(split.Below)} or {VaultAmount.Of(split.Above)}"
                    : $"ask for {VaultAmount.Of(split.Above)}";
                throw ApiErrors.InvalidArgument(
                    $"Entry {entry.Index} of {silo} ({entry.PrefabName}, {VaultAmount.Of(entry.Quantity)}) moves whole " +
                    $"(it holds things, is not a stack, or is food), so quantity {request.Quantity} would split it: {ask}.");
            default:
                throw ApiErrors.Refused("not_in_silo",
                    $"{silo} holds no {request.Prefab.Asked}; it holds {Stored(entries)}.");
        }
    }

    /// <summary>The prefab's quantity over every entry that is it (contents of other entries not counted).</summary>
    internal static double Total(List<SiloEntry> entries, SiloPrefabChoice prefab)
    {
        double total = 0.0;
        foreach (SiloEntry entry in entries)
        {
            total += prefab.Matches(entry.PrefabName, entry.PrefabHash) ? entry.Quantity : 0.0;
        }

        return total;
    }

    /// <summary>Entries the pick empties out of the store.</summary>
    internal static int Leaving(SiloPick.Picked pick)
    {
        int leaving = 0;
        foreach (SiloTake take in pick.Takes)
        {
            leaving += take.Left <= SiloPick.Trace ? 1 : 0;
        }

        return leaving;
    }

    private static string Stored(List<SiloEntry> entries)
    {
        List<string> names = new List<string>();
        int more = 0;
        foreach (SiloEntry entry in entries)
        {
            string name = entry.PrefabName ?? "?";
            if (names.Contains(name))
            {
                continue;
            }

            if (names.Count < NamesListed)
            {
                names.Add(name);
            }
            else
            {
                more++;
            }
        }

        return names.Count == 0 ? "nothing"
            : string.Join(", ", names) + (more > 0 ? $" and {more} more kinds (container_contents lists them)" : string.Empty);
    }
}

/// <summary>
/// One withdrawal's effect on the store: the pooled amount taken front first and the whole entries loaded. Commit
/// rebuilds the store from the entries as read, so it is right after any part of the withdrawal, a failed one too.
/// </summary>
internal sealed class SiloWithdrawal : IWithdrawalSource
{
    private readonly SiloStore _store;
    private readonly List<SiloEntry> _entries;
    private readonly SiloPick.Picked _pick;
    private readonly Delivery _delivery;
    private readonly SiloPlacement _placement;
    private readonly Thing _holder;
    private readonly HashSet<int> _loaded = new HashSet<int>();
    private readonly double[] _quantities;
    private double _pooledTaken;

    internal SiloWithdrawal(SiloStore store, List<SiloEntry> entries, SiloPick.Picked pick, Delivery delivery,
        SiloPlacement placement, Thing holder)
    {
        _store = store;
        _entries = entries;
        _pick = pick;
        _delivery = delivery;
        _placement = placement;
        _holder = holder;
        _quantities = new double[entries.Count];
        for (int index = 0; index < entries.Count; index++)
        {
            _quantities[index] = entries[index].Quantity;
        }
    }

    internal List<SiloTakenView> Taken()
    {
        List<SiloTakenView> taken = new List<SiloTakenView>(_pick.Takes.Count);
        foreach (SiloTake take in _pick.Takes)
        {
            taken.Add(new SiloTakenView(_entries[take.Index].Index, take.Quantity, take.Left, take.Whole,
                _entries[take.Index].Children));
        }

        return taken;
    }

    internal List<PlacedView> Preview()
    {
        List<PlacedView> placed = _delivery.Preview(_placement.Pooled);
        int whole = 0;
        foreach (SiloTake take in _pick.Takes)
        {
            if (take.Whole)
            {
                Slot? slot = WholeSlot(whole++);
                placed.Add(slot != null
                    ? new PlacedView(PlacedView.InSlot, Delivery.RefOf(slot), take.Quantity, null)
                    : new PlacedView(PlacedView.Ground, null, take.Quantity, null));
            }
        }

        return placed;
    }

    internal List<PlacedView> Run(DynamicThing template)
    {
        List<PlacedView> placed = _pick.Pooled > SiloPick.Trace
            ? _delivery.Deliver(_placement.Pooled, (Item)template, this, _pick.Pooled)
            : new List<PlacedView>();
        try
        {
            int whole = 0;
            foreach (SiloTake take in _pick.Takes)
            {
                if (take.Whole)
                {
                    placed.Add(LoadWhole(_entries[take.Index], WholeSlot(whole++), take.Quantity));
                }
            }
        }
        finally
        {
            Commit();
        }

        return placed;
    }

    // The empty slot the n-th whole entry goes into; null for the ground.
    private Slot? WholeSlot(int n) =>
        _placement.Whole.Steps[n] is PlacementStep.NewStack step
            ? _delivery.Empty[_placement.WholeSlotsFrom + step.Slot]
            : null;

    // The entry leaves the store as its root is made (Commit in Run's finally), as the export's dequeue precedes
    // GetStoredThing; a root the game could not make leaves it stored.
    private PlacedView LoadWhole(SiloEntry entry, Slot? slot, double quantity)
    {
        _loaded.Add(entry.Index);
        Vector3 position = slot != null ? HolderChain.PlaceOf(_holder).Position : _delivery.Ground();
        DynamicThing? made = SiloLoader.Load(entry, slot, position);
        if (made == null)
        {
            _loaded.Remove(entry.Index);
            throw ApiErrors.Refused("move_failed",
                $"The game could not load entry {entry.Index} ({entry.PrefabName}) from its save data; it stays in the " +
                "silo. Entries before it were taken (container_contents shows the silo).");
        }

        if (slot != null && made.ParentSlot == slot)
        {
            return new PlacedView(PlacedView.InSlot, Delivery.RefOf(slot), quantity, new ThingId(made.ReferenceId));
        }

        if (slot != null)
        {
            // The slot refused it after all and the game left it in the world: bring it down beside the holder.
            OnServer.MoveToWorld(made, _delivery.Ground(), Quaternion.identity);
        }

        return new PlacedView(PlacedView.Ground, null, quantity, new ThingId(made.ReferenceId));
    }

    public void Take(double quantity)
    {
        _pooledTaken = quantity;
        Commit();
    }

    public void Return(double quantity)
    {
        _pooledTaken = Math.Max(0.0, _pooledTaken - quantity);
        Commit();
    }

    public void Settle()
    {
    }

    // The store as read, less the whole entries loaded and the pooled amount taken front first.
    private void Commit()
    {
        double pooledLeft = _pooledTaken;
        Dictionary<int, double> keptOf = new Dictionary<int, double>();
        foreach (SiloTake take in _pick.Takes)
        {
            if (take.Whole)
            {
                continue;
            }

            double part = Math.Min(pooledLeft, take.Quantity);
            pooledLeft -= part;
            keptOf[take.Index] = _quantities[take.Index] - part;
        }

        List<StoredThings> kept = new List<StoredThings>(_store.Entries.Count);
        int described = 0;
        for (int index = 0; index < _store.Entries.Count; index++)
        {
            StoredThings stored = _store.Entries[index];
            SiloEntry? entry = described < _entries.Count && _entries[described].Index == index
                ? _entries[described++]
                : null;
            if (entry == null)
            {
                kept.Add(stored);
                continue;
            }

            int at = described - 1;
            if (_loaded.Contains(entry.Index))
            {
                continue;
            }

            double quantity = keptOf.TryGetValue(at, out double left) ? left : _quantities[at];
            if (quantity <= SiloPick.Trace)
            {
                continue;
            }

            entry.SetQuantity(quantity);
            kept.Add(stored);
        }

        _store.Replace(kept);
    }
}
