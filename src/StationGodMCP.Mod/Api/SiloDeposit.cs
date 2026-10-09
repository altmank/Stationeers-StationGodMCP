#nullable enable

using System;
using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.Networking;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Chutes;
using Assets.Scripts.Objects.Entities;
using Assets.Scripts.Objects.Items;
using Objects.Items;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;

namespace StationGodMCP.Api;

/// <summary>
/// silo_deposit: put things straight into an SDB Silo's store from wherever they are (a player at any depth, a
/// container, the ground), each as one entry made as the silo's import makes it (SiloSaver: the thing's save data and
/// everything it holds, then the things destroyed), at the back of the store. No chute, import slot or door is
/// involved. For part of a stack the part is stored and taken off the source stack. Writes; host only.
///
/// What the silo takes (CODE): a thing reaches the import slot from a chute (DeviceImport.TryChuteImport, which takes
/// any occupant) or from the input trigger, which takes an Item whose SlotType is the import slot's class, or any Item
/// when that class is None (DeviceImport.OnInputTriggerEnter, DeviceImport.cs:381); this applies the trigger's rule.
/// The silo refuses an import when it holds 600 entries (CanBeginImport, Silo.cs:77); this counts an import the silo
/// is saving as taken.
/// </summary>
internal static class SiloDepositApi
{
    internal static SiloDepositView Handle(Args args)
    {
        if (NetworkManager.IsClient || !GameManager.RunSimulation)
        {
            throw ApiErrors.Refused("not_host", "This game is a multiplayer client; only the host moves items.");
        }

        bool dryRun = WriteMode.IsDryRun(args);
        SiloStore store = SiloStore.Of(Silos.Resolve(args.ThingId("silo_id")));
        SiloDepositForm form = SiloDepositForm.Of(args);
        bool allowInUse = args.OptionalBool("allow_in_use") ?? false;
        store.RequirePowered();
        SiloDepositList list = SiloDepositList.Of(form, args, store.Silo, allowInUse);
        int room = SiloRules.Room(store.Entries.Count, store.Importing);
        BatchBuilder batch = new BatchBuilder(list.Picks.Count);
        List<SiloDepositPlan> plans = new List<SiloDepositPlan>(list.Picks.Count);
        HashSet<long> named = new HashSet<long>();
        for (int index = 0; index < list.Picks.Count; index++)
        {
            SiloDepositPick pick = list.Picks[index];
            ApiException? refusal = SiloDepositPlan.TryPlan(pick, store.Silo, named, plans, allowInUse,
                out SiloDepositPlan? plan);
            if (refusal == null && plans.Count >= room)
            {
                refusal = ApiErrors.Refused("silo_full",
                    $"{Names.Of(store.Silo)} holds {store.Entries.Count} of {SiloRules.Capacity} entries" +
                    (store.Importing ? " and is saving an import" : string.Empty) +
                    $"; {room} more fit, and this deposit already takes them.");
            }

            if (refusal != null)
            {
                batch.Failed(new NotDepositedView(index, pick.Id, list.PrefabOf(index), refusal));
                continue;
            }

            batch.Succeeded(plan!.View(index, store.Entries.Count + plans.Count));
            plans.Add(plan);
        }

        int before = store.Entries.Count;
        if (!dryRun && plans.Count > 0)
        {
            Store(store, plans);
        }

        int after = dryRun ? before + plans.Count : SiloStore.Of(store.Silo).Entries.Count;
        return new SiloDepositView(dryRun, store.View(), list.Matched, list.Skipped, list.Truncated, batch.Build(),
            new SiloCountView(before, after));
    }

    // Each plan becomes an entry at the back; the store is written even when a game call fails part way, so every
    // thing already destroyed is in it.
    private static void Store(SiloStore store, List<SiloDepositPlan> plans)
    {
        List<StoredThings> entries = new List<StoredThings>(store.Entries);
        try
        {
            foreach (SiloDepositPlan plan in plans)
            {
                entries.Add(plan.Apply());
            }
        }
        finally
        {
            store.Replace(entries);
        }
    }
}

/// <summary>The things a deposit names, from its form; for the filter form also what the filter matched and skipped.</summary>
internal sealed class SiloDepositList
{
    private readonly List<string?> _prefabs;

    private SiloDepositList(List<SiloDepositPick> picks, List<string?> prefabs, int? matched, int skipped,
        bool truncated)
    {
        Picks = picks;
        _prefabs = prefabs;
        Matched = matched;
        Skipped = skipped;
        Truncated = truncated;
    }

    internal List<SiloDepositPick> Picks { get; }

    internal int? Matched { get; }

    internal int Skipped { get; }

    internal bool Truncated { get; }

    /// <summary>The prefab the filter form knew up front, for a refusal; null in the id forms.</summary>
    internal string? PrefabOf(int index) => index < _prefabs.Count ? _prefabs[index] : null;

    /// <summary>The filter form leaves out what the silo does not take, and a part a device is using unless
    /// allowInUse; both count as skipped.</summary>
    internal static SiloDepositList Of(SiloDepositForm form, Args args, Silo silo, bool allowInUse)
    {
        if (form is SiloDepositForm.ById ids)
        {
            return new SiloDepositList(ids.Picks, new List<string?>(), null, 0, false);
        }

        int limit = ((SiloDepositForm.ByFilter)form).Limit;
        List<ItemRecord> records = WorldItems.Collect(ItemFilter.Parse(args), PlayerOrigin.Current());
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

        SiloDepositForm.NoteCut(picks.Count, taken.Count);
        return new SiloDepositList(picks, prefabs, records.Count, skipped, taken.Count > limit);
    }
}

/// <summary>What an SDB Silo's import takes (see SiloDepositApi).</summary>
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

    // move_item's rules for taking a thing out of its slot: not a locked slot, not a plant growing in a grower, not a
    // part its device is using unless allow_in_use.
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
