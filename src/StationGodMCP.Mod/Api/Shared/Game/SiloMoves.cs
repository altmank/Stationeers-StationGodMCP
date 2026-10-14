#nullable enable

using System.Collections.Generic;
using System;
using Assets.Scripts.Networking;
using Assets.Scripts.Objects.Chutes;
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

/// <summary>The things a deposit names, from its form; for the filter form also what the filter matched and skipped.</summary>
internal sealed class SiloDepositList
{
    private readonly List<string?> _prefabs;

    private SiloDepositList(List<SiloDepositPick> picks, List<string?> prefabs, int? matched, int skipped, int taken)
    {
        Picks = picks;
        _prefabs = prefabs;
        Matched = matched;
        Skipped = skipped;
        Taken = taken;
    }

    /// <summary>Things by id, in the order given.</summary>
    internal static SiloDepositList ById(List<SiloDepositPick> picks) =>
        new SiloDepositList(picks, new List<string?>(), null, 0, picks.Count);

    internal List<SiloDepositPick> Picks { get; }

    internal int? Matched { get; }

    internal int Skipped { get; }

    /// <summary>How many things the silo would take: more than Picks when the limit cut the list.</summary>
    internal int Taken { get; }

    internal bool Truncated => Taken > Picks.Count;

    /// <summary>The prefab the filter form knew up front, for a refusal; null in the id forms.</summary>
    internal string? PrefabOf(int index) => index < _prefabs.Count ? _prefabs[index] : null;

    /// <summary>The filter form leaves out what the silo does not take, and a part a device is using unless
    /// allowInUse; both count as skipped.</summary>
    internal static SiloDepositList Nearest(ItemFilter filter, int limit, Silo silo, bool allowInUse)
    {
        List<ItemRecord> records = WorldItems.Collect(filter, PlayerOrigin.Current());
        List<ItemRecord> taken = new List<ItemRecord>(records.Count);
        int skipped = 0;
        foreach (ItemRecord record in records)
        {
            if (SiloImportRule.Refusal(record.Item, silo) == null &&
                PartsInUse.Refusal(record.Item, allowInUse) == null)
            {
                taken.Add(record);
            }
            else
            {
                skipped++;
            }
        }

        taken.Sort(ItemRecord.NearestFirst);
        int count = Math.Min(limit, taken.Count);
        List<SiloDepositPick> picks = new List<SiloDepositPick>(count);
        List<string?> prefabs = new List<string?>(count);
        for (int index = 0; index < count; index++)
        {
            picks.Add(new SiloDepositPick(new ThingId(taken[index].Item.ReferenceId), null));
            prefabs.Add(taken[index].Item.PrefabName);
        }

        return new SiloDepositList(picks, prefabs, records.Count, skipped, taken.Count);
    }
}

/// <summary>
/// What an SDB Silo's import takes (CODE): the input trigger takes an Item whose SlotType is the import slot's class,
/// or any Item when that class is None (DeviceImport.OnInputTriggerEnter).
/// </summary>
internal static class SiloImportRule
{
    internal static ApiException? Refusal(DynamicThing thing, Silo silo)
    {
        Slot import = silo.ImportSlot;
        if (!(thing is Item) || thing is Organ)
        {
            return ApiErrors.Refused("slot_refuses",
                $"{Names.Of(thing)} is not an item, and the silo's import takes items only.");
        }

        return import != null && import.Type != Slot.Class.None && thing.SlotType != import.Type
            ? ApiErrors.Refused("slot_refuses",
                $"The silo's import slot takes {import.Type} items; {Names.Of(thing)} is {thing.SlotType}.")
            : null;
    }
}

/// <summary>One thing checked against the silo's and the move rules, with the amount it stores.</summary>
internal sealed class SiloDepositPlan
{
    private SiloDepositPlan(DynamicThing thing, double quantity, double held, int children)
    {
        Thing = thing;
        Quantity = quantity;
        Held = held;
        Children = children;
        From = thing.ParentSlot != null
            ? new SlotRefView(new ThingId(thing.ParentSlot.Parent.ReferenceId), thing.ParentSlot.SlotIndex)
            : null;
    }

    internal DynamicThing Thing { get; }

    private double Quantity { get; }

    private double Held { get; }

    private int Children { get; }

    private SlotRefView? From { get; }

    private bool IsWhole => Quantity >= Held - SiloPick.Trace;

    /// <summary>A refusal, or null and the plan.</summary>
    internal static ApiException? TryPlan(SiloDepositPick pick, Silo silo, HashSet<long> named,
        List<SiloDepositPlan> planned, bool allowInUse, out SiloDepositPlan? plan)
    {
        plan = null;
        ApiException? refusal = ThingRefusal(pick, silo, named, allowInUse, out DynamicThing? thing) ??
                                OverlapRefusal(thing!, planned);
        if (refusal != null)
        {
            return refusal;
        }

        List<DynamicThing> children = SiloSaver.Children(thing!);
        foreach (DynamicThing child in children)
        {
            if (child is Human)
            {
                return ApiErrors.Refused("not_movable", $"{Names.Of(thing!)} holds a player.");
            }
        }

        double held = HeldIn(thing!);
        double quantity = pick.Quantity ?? held;
        refusal = QuantityRefusal(thing!, quantity, held);
        if (refusal != null)
        {
            return refusal;
        }

        plan = new SiloDepositPlan(thing!, quantity, held, children.Count);
        return null;
    }

    private static ApiException? ThingRefusal(SiloDepositPick pick, Silo silo, HashSet<long> named, bool allowInUse,
        out DynamicThing? thing)
    {
        thing = null;
        if (!GameLookup.TryFindThing(pick.Id, out Thing found) || found.IsBeingDestroyed)
        {
            return ApiErrors.ThingNotFound(pick.Id);
        }

        if (!named.Add(found.ReferenceId))
        {
            return ApiErrors.InvalidArgument($"{Names.Of(found)} ({pick.Id}) is named twice.");
        }

        thing = found as DynamicThing;
        if (thing == null)
        {
            return ApiErrors.Refused("not_movable", $"{Names.Of(found)} is not an item that fits in a slot.");
        }

        Silo? handling = Silos.HandlingOf(thing);
        if (handling != null)
        {
            // The silo may be saving or spawning it right now (its _saveStack, _spawningThings); let it finish.
            return ApiErrors.Refused("silo_busy",
                $"{Names.Of(thing)} is in a slot of {Names.Of(handling)} ({handling.ReferenceId}), which is importing " +
                "or exporting it; take it out or let the silo finish.");
        }

        Slot? slot = thing.ParentSlot;
        if (slot != null && slot.Parent != null && (IngotVaults.IsVault(slot.Parent) || IngotVaults.IsRemote(slot.Parent)))
        {
            return ApiErrors.Refused("in_vault_slot",
                $"{Names.Of(thing)} is in a slot of {Names.Of(slot.Parent)}; take it out or let the vault finish.");
        }

        return SourceRefusal(thing, allowInUse) ?? SiloImportRule.Refusal(thing, silo);
    }

    // The slot move's rules for taking a thing out of its slot: not a locked slot, not a plant growing in a grower, not a
    // part its device is using unless allow: ["in_use"].
    private static ApiException? SourceRefusal(DynamicThing thing, bool allowInUse)
    {
        Slot? from = thing.ParentSlot;
        if (from == null)
        {
            return null;
        }

        if (from.IsLocked)
        {
            return ApiErrors.Refused("slot_locked", $"{Names.Of(thing)} is in a locked slot.");
        }

        return GrowerSlotRule.TakesOut(GrowerSlots.KindOf(from.Parent, from), thing is Plant, thing is Seed)
            ? PartsInUse.Refusal(thing, allowInUse)
            : ApiErrors.Refused("planted",
                $"{Names.Of(thing)} is growing in {SlotAccess.Label(from)}: a player never takes a plant out whole.");
    }

    // A thing stored with its holder, or holding one already planned, would be stored twice.
    private static ApiException? OverlapRefusal(DynamicThing thing, List<SiloDepositPlan> planned)
    {
        foreach (SiloDepositPlan other in planned)
        {
            if (Holds(other.Thing, thing))
            {
                return ApiErrors.InvalidArgument(
                    $"{Names.Of(thing)} is inside {Names.Of(other.Thing)}, which this deposit already stores with it.");
            }

            if (Holds(thing, other.Thing))
            {
                return ApiErrors.InvalidArgument(
                    $"{Names.Of(thing)} holds {Names.Of(other.Thing)}, which this deposit already stores; name only one.");
            }
        }

        return null;
    }

    private static bool Holds(DynamicThing holder, DynamicThing thing)
    {
        Slot? slot = thing.ParentSlot;
        for (int depth = 0; slot != null && slot.Parent != null && depth < 16; depth++)
        {
            if (slot.Parent == holder)
            {
                return true;
            }

            slot = slot.Parent is DynamicThing next ? next.ParentSlot : null;
        }

        return false;
    }

    // A stack's count or amount; 1 for anything else.
    private static double HeldIn(DynamicThing thing) =>
        thing switch
        {
            Stackable stack => stack.Quantity,
            Consumable consumable => consumable.Quantity,
            _ => 1.0
        };

    private static ApiException? QuantityRefusal(DynamicThing thing, double quantity, double held)
    {
        if (quantity > held + SiloPick.Trace)
        {
            return ApiErrors.InvalidArgument($"{Names.Of(thing)} holds {held}; cannot take {quantity}.");
        }

        if (quantity < held - SiloPick.Trace && !(thing is Stackable || thing is Consumable))
        {
            return ApiErrors.InvalidArgument($"{Names.Of(thing)} is not a stack; it is stored whole (quantity 1).");
        }

        return thing is Stackable && Math.Abs(quantity - Math.Round(quantity)) > SiloPick.Trace
            ? ApiErrors.InvalidArgument($"{Names.Of(thing)} counts whole items; quantity {quantity} is not whole.")
            : null;
    }

    internal SiloDepositedView View(int index, int entry) =>
        new SiloDepositedView(index, GameLookup.ViewOf(Thing), From, Quantity, IsWhole ? 0.0 : Held - Quantity,
            Children, entry);

    /// <summary>The entry, made as the import makes it; for part of a stack, the part taken off the source.</summary>
    internal StoredThings Apply()
    {
        if (IsWhole)
        {
            return SiloSaver.Save(Thing, null);
        }

        StoredThings entry = SiloSaver.Save(Thing, Quantity);
        if (Thing is Stackable stack)
        {
            stack.RemoveQuantity((int)Math.Round(Quantity));
        }
        else
        {
            Consumable consumable = (Consumable)Thing;
            consumable.Quantity -= (float)Quantity;
        }

        return entry;
    }
}

/// <summary>Which entries a request takes, with the silo's own words when it cannot.</summary>
internal static class SiloPicks
{
    private const int NamesListed = 12;

    internal static SiloPick.Picked Choose(SiloStore store, List<SiloEntry> entries, SiloPrefabChoice prefab,
        double quantity)
    {
        List<SiloEntryFacts> facts = new List<SiloEntryFacts>(entries.Count);
        foreach (SiloEntry entry in entries)
        {
            facts.Add(new SiloEntryFacts(prefab.Matches(entry.PrefabName, entry.PrefabHash), entry.Quantity,
                entry.Pooled));
        }

        string silo = Names.Of(store.Silo);
        switch (SiloPick.Choose(facts, quantity))
        {
            case SiloPick.Picked picked:
                return picked;
            case SiloPick.NotEnough short_:
                throw ApiErrors.Refused("not_enough_stock",
                    $"{silo} holds {VaultAmount.Of(short_.Available)} {prefab.Asked}; cannot take " +
                    $"{quantity}.");
            case SiloPick.SplitsEntry split:
                SiloEntry entry = entries[split.Index];
                string ask = split.Below > SiloPick.Trace
                    ? $"ask for {VaultAmount.Of(split.Below)} or {VaultAmount.Of(split.Above)}"
                    : $"ask for {VaultAmount.Of(split.Above)}";
                throw ApiErrors.InvalidArgument(
                    $"Entry {entry.Index} of {silo} ({entry.PrefabName}, {VaultAmount.Of(entry.Quantity)}) moves whole " +
                    $"(it holds things, is not a stack, or is food), so quantity {quantity} would split it: {ask}.");
            default:
                throw ApiErrors.Refused("not_in_silo",
                    $"{silo} holds no {prefab.Asked}; it holds {Stored(entries)}.");
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
            : string.Join(", ", names) + (more > 0 ? $" and {more} more kinds " +
                "(inspect contents lists them)" : string.Empty);
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
                $"The game could not load entry {entry.Index} ({entry.PrefabName}) from its save data; it stays in " +
                "the silo. Entries before it were taken (inspect contents shows the silo).");
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
