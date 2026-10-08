#nullable enable

using System;
using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.Networking;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Chutes;
using Assets.Scripts.Objects.Items;
using Assets.Scripts.Serialization;
using Objects.Items;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using UnityEngine;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// SDB Silos (Assets.Scripts.Objects.Chutes.Silo, CODE build 24703804). A silo keeps what it imports as save data, not
/// as things: private Queue&lt;StoredThings&gt; _storedItems (Silo.cs:18), at most 600 entries (Silo.cs:22, IsFull
/// Silo.cs:89). An entry is one imported thing: StoredThings.DynamicThing, its ToSiloData (DynamicThing.SerializeSave,
/// DynamicThing.cs:1325), and StoredChildren, everything in its slots at any depth, depth first, each saved with its
/// old ParentReferenceId (Silo.PushChildrenForSave, Silo.cs:352). A stack is one entry; its quantity is
/// StackableSaveData.Quantity (Silo.RebuildContentsStack, Silo.cs:568) or ConsumableSaveData.Quantity.
///
/// The store is host state: clients get only the count (TotalItemsCurrentlyStored, [ByteArraySync], network flag 512,
/// Silo.cs:61-75), so on a client a silo's entries are unknown.
/// </summary>
internal static class Silos
{
    private static readonly GameMember[] Needed =
    {
        GameMembers.SiloStoredItems, GameMembers.SiloStackDirty, GameMembers.SiloDoneSaving,
        GameMembers.SiloDoneSpawning, GameMembers.SiloDispenseSlot
    };

    /// <summary>Whether this game keeps silo stores: the host (or single player); a client holds only counts.</summary>
    internal static bool StoresKnown => GameManager.RunSimulation && !NetworkManager.IsClient;

    /// <summary>The silo an id names; not_a_silo for anything else.</summary>
    internal static Silo Resolve(ThingId id)
    {
        Thing thing = GameLookup.RequireThing(id);
        if (thing.IsBeingDestroyed)
        {
            throw ApiErrors.ThingNotFound(id);
        }

        return thing is Silo silo
            ? silo
            : throw ApiErrors.Refused("not_a_silo", $"{Names.Of(thing)} ({id}) is not an SDB Silo.");
    }

    /// <summary>Every built silo in the world, in structure order.</summary>
    internal static List<Silo> All()
    {
        if (!(ThingIndex.ThingsOfClass<Silo>() is { } indexed))
        {
            return Walk();
        }

        List<Silo> silos = new List<Silo>();
        foreach (Thing thing in indexed)
        {
            if (thing is Silo silo && silo != null && !silo.IsCursor && !silo.IsBeingDestroyed)
            {
                silos.Add(silo);
            }
        }

        if (ThingIndex.Verify)
        {
            ThingIndex.VerifyClass(silos, Walk());
        }

        return silos;
    }

    // Every structure the game has, for its silos: what the prefab index answers without the walk.
    private static List<Silo> Walk()
    {
        List<Structure> structures = GridController.AllStructuresPool.ToList();
        List<Silo> silos = new List<Silo>();
        for (int index = 0; index < structures.Count; index++)
        {
            if (structures[index] is Silo silo && silo != null && !silo.IsCursor && !silo.IsBeingDestroyed)
            {
                silos.Add(silo);
            }
        }

        return silos;
    }

    /// <summary>Refuses (game_changed) when this game build lacks a member the silo tools read or write.</summary>
    internal static void RequireMembers()
    {
        foreach (GameMember member in Needed)
        {
            if (!member.TryResolve())
            {
                throw new GameChangedException(member.Name);
            }
        }
    }

    /// <summary>Whether a thing sits in a silo's slot at any depth (being imported or exported by it).</summary>
    internal static Silo? HandlingOf(DynamicThing thing)
    {
        Slot? slot = thing.ParentSlot;
        for (int depth = 0; slot != null && slot.Parent != null && depth < 16; depth++)
        {
            if (slot.Parent is Silo silo)
            {
                return silo;
            }

            slot = slot.Parent is DynamicThing holder ? holder.ParentSlot : null;
        }

        return null;
    }

    /// <summary>
    /// thing_not_found for an id nothing in the world has; when a silo stores the thing that had that id (an entry keeps
    /// its old ReferenceId in its save data until it is loaded), the refusal names the silo and silo_withdraw.
    /// </summary>
    internal static ApiException NotFound(ThingId id)
    {
        if (!StoresKnown || !GameMembers.SiloStoredItems.TryResolve())
        {
            return ApiErrors.ThingNotFound(id);
        }

        foreach (Silo silo in All())
        {
            List<StoredThings> entries = SiloStore.EntriesOf(silo);
            for (int index = 0; index < entries.Count; index++)
            {
                DynamicThingSaveData? root = entries[index]?.DynamicThing;
                if (root != null && root.ReferenceId == id.Value)
                {
                    return ApiErrors.Refused(ApiErrors.ThingNotFoundCode,
                        $"Nothing with reference id {id} is in the world: {Names.Of(silo)} ({silo.ReferenceId}) stores it " +
                        $"as entry {index} ({root.PrefabName}). silo_withdraw takes it out.");
                }
            }
        }

        return ApiErrors.ThingNotFound(id);
    }
}

/// <summary>One silo's store and progress flags, read and written as the silo itself does.</summary>
internal sealed class SiloStore
{
    private SiloStore(Silo silo, List<StoredThings> entries)
    {
        Silo = silo;
        Entries = entries;
    }

    internal Silo Silo { get; }

    /// <summary>The store when read, front (exported next) first.</summary>
    internal List<StoredThings> Entries { get; }

    internal static SiloStore Of(Silo silo)
    {
        Silos.RequireMembers();
        return new SiloStore(silo, EntriesOf(silo));
    }

    internal static List<StoredThings> EntriesOf(Silo silo) =>
        GameMembers.SiloStoredItems.GetValue(silo) is Queue<StoredThings> queue
            ? new List<StoredThings>(queue)
            : throw new GameChangedException(GameMembers.SiloStoredItems.Name);

    /// <summary>
    /// The silo is saving an import (Silo._doneSaving false from TryProcessImport until TickSaveImport enqueues it,
    /// Silo.cs:329-402).
    /// </summary>
    internal bool Importing => !(bool)GameMembers.SiloDoneSaving.GetValue(Silo);

    /// <summary>An exported thing waits in the export slot, or its children are still spawning (Silo.cs:193).</summary>
    internal bool Exporting => !(bool)GameMembers.SiloDoneSpawning.GetValue(Silo) || Silo.ExportingThing != null;

    /// <summary>The entry an IC asked for with DispenseSlot, -1 for none (Silo.SetLogicValue, Silo.cs:503).</summary>
    internal int DispenseSlot => (int)GameMembers.SiloDispenseSlot.GetValue(Silo);

    internal SiloBusyView Busy() => new SiloBusyView(Importing, Exporting, DispenseSlot);

    /// <summary>
    /// The store becomes these entries, front first, then the silo refreshes as after its own import and export: its
    /// IC-readable stack is rebuilt on its next logic tick (_stackDirty, OnLogicTick Silo.cs:541) and its count is set
    /// and sent to clients (UpdateTotalItemsCurrentlyStored, Silo.cs:416). The field gets a new queue rather than
    /// having the old one emptied and refilled, so a logic tick enumerating the old one (RebuildContentsStack runs on
    /// the logic thread) finishes on a queue nobody changes.
    /// </summary>
    internal void Replace(List<StoredThings> entries)
    {
        Queue<StoredThings> queue = new Queue<StoredThings>(Math.Max(entries.Count, SiloRules.Capacity));
        foreach (StoredThings entry in entries)
        {
            queue.Enqueue(entry);
        }

        GameMembers.SiloStoredItems.SetValue(Silo, queue);
        GameMembers.SiloStackDirty.SetValue(Silo, true);
        Silo.UpdateTotalItemsCurrentlyStored();
    }

    /// <summary>The silo's own rule for both: it imports and exports only when built, on and powered (Silo.cs:157-181).</summary>
    internal void RequirePowered()
    {
        if (!Silo.IsStructureCompleted || !Silo.OnOff || !Silo.Powered)
        {
            string state = !Silo.IsStructureCompleted ? "not finished" : Silo.OnOff ? "unpowered" : "off";
            throw ApiErrors.Refused("silo_unpowered",
                $"{Names.Of(Silo)} ({Silo.ReferenceId}) is {state}; the silo neither imports nor exports then. " +
                "Nothing was changed.");
        }
    }

    internal SiloRefView View() => new SiloRefView(GameLookup.ViewOf(Silo), Silo.OnOff, Silo.Powered);

    /// <summary>The entries described, front first.</summary>
    internal List<SiloEntry> Describe()
    {
        List<SiloEntry> described = new List<SiloEntry>(Entries.Count);
        for (int index = 0; index < Entries.Count; index++)
        {
            if (Entries[index]?.DynamicThing != null)
            {
                described.Add(new SiloEntry(index, Entries[index]));
            }
        }

        return described;
    }
}

/// <summary>One stored entry, read from its save data and its prefab.</summary>
internal sealed class SiloEntry
{
    internal SiloEntry(int index, StoredThings stored)
    {
        Index = index;
        Stored = stored;
        Root = stored.DynamicThing;
        Template = string.IsNullOrEmpty(Root.PrefabName) ? null : Prefab.Find(Root.PrefabName) as DynamicThing;
        Quantity = QuantityOf(Root);
        CountsQuantity = Root is StackableSaveData || Root is ConsumableSaveData;
        Children = stored.StoredChildren?.Count ?? 0;
        Rots = Template is Item item && SiloRules.RotsWhenTaken(Template is INutrition, item.CanDecay, Template is Seed);
    }

    /// <summary>The entry's place in the store when read.</summary>
    internal int Index { get; }

    internal StoredThings Stored { get; }

    internal DynamicThingSaveData Root { get; }

    /// <summary>The prefab its saved PrefabName names; null when this game has no such prefab.</summary>
    internal DynamicThing? Template { get; }

    internal string? PrefabName => Root.PrefabName;

    /// <summary>The hash the silo's logic stack gives it (Animator.StringToHash of PrefabName, Silo.cs:567).</summary>
    internal int PrefabHash => string.IsNullOrEmpty(Root.PrefabName) ? 0 : Animator.StringToHash(Root.PrefabName);

    /// <summary>The Labeller name it was stored with, else its prefab's name.</summary>
    internal string? DisplayName =>
        Root.IsCustomName && !string.IsNullOrEmpty(Root.CustomName) ? Root.CustomName :
        Template != null ? Template.DisplayName : Root.PrefabName;

    internal double Quantity { get; }

    internal double? MaxQuantity =>
        Template switch
        {
            Stackable stackable => stackable.MaxQuantity,
            Consumable consumable => consumable.MaxQuantity,
            _ => null
        };

    /// <summary>A stack: its quantity is a count (Stackable) or an amount (Consumable).</summary>
    internal bool CountsQuantity { get; }

    internal int Children { get; }

    /// <summary>The silo's export rule spoils it (SiloRules.RotsWhenTaken).</summary>
    internal bool Rots { get; }

    internal bool Pooled => SiloRules.Pools(CountsQuantity, Children, Rots);

    internal static double QuantityOf(DynamicThingSaveData data) =>
        data switch
        {
            StackableSaveData stack => stack.Quantity,
            ConsumableSaveData consumable => consumable.Quantity,
            _ => 1.0
        };

    /// <summary>Sets a stack entry's quantity, as the silo's own stack records it.</summary>
    internal void SetQuantity(double quantity)
    {
        switch (Root)
        {
            case StackableSaveData stack:
                stack.Quantity = (int)Math.Round(quantity);
                break;
            case ConsumableSaveData consumable:
                consumable.Quantity = (float)quantity;
                break;
        }
    }

    /// <summary>What it holds, summed per prefab in the order first stored.</summary>
    internal List<SiloContentView> Contents()
    {
        List<SiloContentView> contents = new List<SiloContentView>();
        if (Stored.StoredChildren == null)
        {
            return contents;
        }

        List<string?> names = new List<string?>();
        List<double> amounts = new List<double>();
        foreach (DynamicThingSaveData child in Stored.StoredChildren)
        {
            if (child == null)
            {
                continue;
            }

            int at = names.IndexOf(child.PrefabName);
            if (at < 0)
            {
                names.Add(child.PrefabName);
                amounts.Add(QuantityOf(child));
            }
            else
            {
                amounts[at] += QuantityOf(child);
            }
        }

        for (int index = 0; index < names.Count; index++)
        {
            contents.Add(new SiloContentView(names[index], amounts[index]));
        }

        return contents;
    }

    internal SiloEntryView View() =>
        new SiloEntryView(Index, PrefabName, DisplayName, Quantity, MaxQuantity, Children, Contents(), Rots);
}

/// <summary>
/// Loads a stored entry back into the world as the silo's export does (Silo.BeginExport and GetStoredThing,
/// Silo.cs:252-319), but into a chosen slot instead of the export slot and with its children at once.
///
/// As GetStoredThing: a saved ParentReferenceId is remapped through the ids the entry's things got so far, the
/// ReferenceId is cleared, XmlSaveLoad.Load makes the thing (Thing.Create registers it and, on a server with clients,
/// adds it to Thing.NewToSend, which is how clients get it), and DynamicThing.DeserializeSave moves it into the slot its
/// save data names (MoveToParent). As SetDefaultSlotExport (Silo.cs:321) sets the export slot, the root's save data is
/// pointed at the target slot (or the ground). The food rule is BeginExport's (Silo.cs:262), on the root only.
///
/// The remap is a map of its own per entry, not the silo's _changedIds: that one belongs to an export the silo may be
/// running at the same time (TickSpawnExport keeps remapping its children through it).
///
/// Children at once rather than one per 0.2 s (TickSpawnExport, Silo.cs:269): nothing in GetStoredThing or
/// DeserializeSave waits on time. A child's parent is registered by Thing.Create in the same call, so MoveToParent
/// finds it at once (Referencable.Find) and the child goes straight into its slot, as a save load makes a whole world's
/// things in one pass. The export's interval only paces things coming out of its export slot.
/// </summary>
internal static class SiloLoader
{
    /// <summary>The root made, in the slot (or on the ground at position with slot null); null when the game made nothing.</summary>
    internal static DynamicThing? Load(SiloEntry entry, Slot? slot, Vector3 position)
    {
        Dictionary<long, long> changed = new Dictionary<long, long>();
        DynamicThingSaveData root = entry.Root;
        Place(root, position);
        root.ParentReferenceId = slot != null ? slot.Parent.ReferenceId : 0L;
        root.ParentSlotId = slot != null ? slot.SlotIndex : 0;
        DynamicThing? made = LoadOne(root, changed);
        if (made == null)
        {
            return null;
        }

        if (entry.Rots)
        {
            made.DamageState.Damage(ChangeDamageType.Increment, made.DamageState.MaxDamage + 1f, DamageUpdateType.Decay);
        }

        if (entry.Stored.StoredChildren != null)
        {
            foreach (DynamicThingSaveData child in entry.Stored.StoredChildren)
            {
                if (child != null)
                {
                    Place(child, position);
                    LoadOne(child, changed);
                }
            }
        }

        return made;
    }

    // Where a thing appears if it is not in a slot; never dragged or moving.
    private static void Place(DynamicThingSaveData data, Vector3 position)
    {
        data.WorldPosition = position;
        data.WorldRotation = Quaternion.identity;
        data.Dragged = false;
        data.Velocity = Vector3.zero;
        data.AngularVelocity = Vector3.zero;
    }

    // Silo.GetStoredThing (Silo.cs:292-319), with the entry's own id map.
    private static DynamicThing? LoadOne(DynamicThingSaveData data, Dictionary<long, long> changed)
    {
        if (changed.TryGetValue(data.ParentReferenceId, out long parent))
        {
            data.ParentReferenceId = parent;
        }

        long stored = data.ReferenceId;
        data.ReferenceId = 0L;
        DynamicThing thing = XmlSaveLoad.Load<DynamicThing>(data);
        if (!thing)
        {
            return null;
        }

        if (thing.ReferenceId == 0L)
        {
            Referencable.RegisterNew(thing);
            if (NetworkManager.IsServer && NetworkBase.Clients.Count > 0)
            {
                Thing.NewToSend.Add(thing);
            }
        }

        changed[stored] = thing.ReferenceId;
        return thing;
    }
}

/// <summary>
/// Stores a thing as one entry, as the silo's import does (Silo.SaveThings and TickSaveImport, Silo.cs:338-402): the
/// thing's ToSiloData, then everything in its slots depth first (PushChildrenForSave pushes slots last to first, so
/// slot 0's occupant and its contents come first), then the children destroyed last saved first and the thing itself
/// last (DestroySavedThing: OnServer.Destroy, then the GameObject), all in one call rather than one per tick.
/// </summary>
internal static class SiloSaver
{
    /// <summary>The entry for a whole thing; quantity set only for part of a stack.</summary>
    internal static StoredThings Save(DynamicThing thing, double? partQuantity)
    {
        StoredThings entry = new StoredThings
        {
            StoredChildren = new List<DynamicThingSaveData>(),
            DynamicThing = thing.ToSiloData()
        };
        if (partQuantity.HasValue)
        {
            switch (entry.DynamicThing)
            {
                case StackableSaveData stack:
                    stack.Quantity = (int)Math.Round(partQuantity.Value);
                    break;
                case ConsumableSaveData consumable:
                    consumable.Quantity = (float)partQuantity.Value;
                    break;
            }

            return entry;
        }

        List<DynamicThing> saved = Children(thing);
        foreach (DynamicThing child in saved)
        {
            entry.StoredChildren.Add(child.ToSiloData());
        }

        for (int index = saved.Count - 1; index >= 0; index--)
        {
            Destroy(saved[index]);
        }

        Destroy(thing);
        return entry;
    }

    /// <summary>Everything in a thing's slots at any depth, in the order the import saves them.</summary>
    internal static List<DynamicThing> Children(DynamicThing thing)
    {
        List<DynamicThing> children = new List<DynamicThing>();
        Stack<DynamicThing> pending = new Stack<DynamicThing>();
        Push(pending, thing.Slots);
        while (pending.Count > 0)
        {
            DynamicThing child = pending.Pop();
            if (child != null && child.gameObject != null)
            {
                children.Add(child);
                Push(pending, child.Slots);
            }
        }

        return children;
    }

    private static void Push(Stack<DynamicThing> pending, List<Slot>? slots)
    {
        if (slots == null)
        {
            return;
        }

        for (int index = slots.Count - 1; index >= 0; index--)
        {
            DynamicThing occupant = slots[index].Get();
            if (occupant != null)
            {
                pending.Push(occupant);
            }
        }
    }

    // Silo.DestroySavedThing (Silo.cs:404).
    private static void Destroy(DynamicThing thing)
    {
        if (thing == null || thing.gameObject == null)
        {
            return;
        }

        OnServer.Destroy(thing);
        if (thing.gameObject)
        {
            UnityEngine.Object.Destroy(thing.gameObject);
        }
    }
}

/// <summary>
/// Silo stock as find_items and item_totals count it: every entry's stored thing and every thing stored inside one
/// (as the item tools count a backpack's contents in a locker), located in the silo. Host only: a client holds no
/// store, so it lists none.
/// </summary>
internal static class SiloStock
{
    internal const string Location = SiloPlaceView.Location;

    /// <summary>Every stored thing the filter keeps, silo by silo, front first.</summary>
    internal static List<SiloRecord> Collect(ItemFilter filter, PlayerOrigin origin)
    {
        List<SiloRecord> records = new List<SiloRecord>();
        if (!filter.WantsSilo || !Silos.StoresKnown || !GameMembers.SiloStoredItems.TryResolve())
        {
            return records;
        }

        foreach (Silo silo in Silos.All())
        {
            double? distance = origin.ExactDistanceTo(silo.Position);
            List<StoredThings> entries = SiloStore.EntriesOf(silo);
            for (int index = 0; index < entries.Count; index++)
            {
                StoredThings? entry = entries[index];
                if (entry?.DynamicThing == null)
                {
                    continue;
                }

                Add(records, filter, new SiloRecord(silo, index, entry.DynamicThing, null, distance));
                if (entry.StoredChildren == null)
                {
                    continue;
                }

                foreach (DynamicThingSaveData child in entry.StoredChildren)
                {
                    if (child != null)
                    {
                        Add(records, filter, new SiloRecord(silo, index, child, entry.DynamicThing.PrefabName, distance));
                    }
                }
            }
        }

        return records;
    }

    private static void Add(List<SiloRecord> records, ItemFilter filter, SiloRecord record)
    {
        if (filter.Keeps(record))
        {
            records.Add(record);
        }
    }
}

/// <summary>One stored thing in a silo: the entry's own, or one inside it.</summary>
internal sealed class SiloRecord
{
    internal SiloRecord(Silo silo, int entry, DynamicThingSaveData data, string? inside, double? distance)
    {
        Silo = silo;
        Entry = entry;
        Inside = inside;
        Distance = distance;
        PrefabName = data.PrefabName;
        DynamicThing? template = string.IsNullOrEmpty(data.PrefabName) ? null : Prefab.Find(data.PrefabName) as DynamicThing;
        DisplayName = data.IsCustomName && !string.IsNullOrEmpty(data.CustomName) ? data.CustomName :
            template != null ? template.DisplayName : data.PrefabName;
        Quantity = SiloEntry.QuantityOf(data);
        MaxQuantity = template switch
        {
            Stackable stackable => stackable.MaxQuantity,
            Consumable consumable => consumable.MaxQuantity,
            _ => (double?)null
        };
    }

    internal Silo Silo { get; }

    internal int Entry { get; }

    /// <summary>The entry's stored thing's prefab when this is inside it; null for the entry's own thing.</summary>
    internal string? Inside { get; }

    internal double? Distance { get; }

    internal string? PrefabName { get; }

    internal string? DisplayName { get; }

    internal double Quantity { get; }

    internal double? MaxQuantity { get; }

    internal SiloItemView ToView()
    {
        double? distance = Distance.HasValue ? Math.Round(Distance.Value, PositionView.Decimals) : null;
        List<HeldInView> heldIn = new List<HeldInView>
        {
            new HeldInView(GameLookup.ViewOf(Silo), SiloItemView.NoSlot, SiloItemView.StoreSlotName)
        };
        return new SiloItemView(PrefabName, DisplayName, Quantity, MaxQuantity, heldIn, GameLookup.ViewOf(Silo.Position),
            distance, new SiloPlaceView(Entry, Inside));
    }
}
