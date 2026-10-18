#nullable enable

using System.Collections.Generic;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Chutes;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using UnityEngine;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// A thing's slots as inspect contents reads them (Thing.Slots, organ slots left out) and what is in each (Slot.Get),
/// following occupants' own slots down to depth levels, in the form asked. Empty slots are left out at every depth
/// unless includeEmpty, the holder's own counted. On an SDB Silo also its store (Silos), whose entries are save data
/// rather than things, one page of them. Read only.
/// </summary>
internal static class ThingContents
{
    /// <summary>
    /// The thing's slots, kept by filter, and a silo's entries from offset, at most limit (KeyedPage: an entry's key is
    /// its place in the store; a walk's later page takes the entries at its kept places as they are now).
    /// </summary>
    internal static ContainerContentsView Read(Thing thing, int depth, bool includeEmpty, SlotForm form,
        SlotFilter filter, WalkRequest walk, int offset, int limit)
    {
        PlayerOrigin origin = PlayerOrigin.Current();
        Vector3 position = HolderChain.PlaceOf(thing).Position;
        List<SlotView> slots = SlotsOf(thing, depth, includeEmpty);
        ShownSlots shown = includeEmpty ? ShownSlots.All(slots) : ShownSlots.Occupied(slots);
        return new ContainerContentsView(GameLookup.ViewOf(thing), GameLookup.ViewOf(position),
            origin.DistanceTo(position), shown.Filtered(filter),
            thing is Silo silo
                ? SiloContents(silo, filter, walk, PageRequest.Of(offset, limit, Pure.SiloRules.Capacity))
                : null,
            form);
    }

    // A silo's store, after its own slots: entries front first (filtered as the slots are), one page of them. On a
    // client the store is unknown (the host keeps it) and only the synced count is shown.
    private static SiloContentsView SiloContents(Silo silo, SlotFilter filter, WalkRequest walk, PageRequest page)
    {
        if (!Silos.StoresKnown)
        {
            return new SiloContentsView(silo.TotalItemsCurrentlyStored, false, null,
                Slice<SiloEntryView>.Page(new List<SiloEntryView>(), page, 0));
        }

        SiloStore store = SiloStore.Of(silo);
        List<SiloEntry> described = store.Describe();
        System.Func<long, SiloEntryView?> describe = place =>
            place >= 0 && place < described.Count ? described[(int)place].View() : null;
        Slice<SiloEntryView> slice;
        if (walk.Continues)
        {
            slice = KeyedPage.Later(walk, page.Limit, describe);
        }
        else
        {
            List<long> kept = new List<long>(described.Count);
            for (int place = 0; place < described.Count; place++)
            {
                if (filter.Keeps(described[place].View()))
                {
                    kept.Add(place);
                }
            }

            slice = KeyedPage.First(walk, kept.ToArray(), page.Offset, page.Limit, describe);
        }
        return new SiloContentsView(store.Entries.Count, true, store.Busy(), slice);
    }

    private static List<SlotView> SlotsOf(Thing thing, int depth, bool includeEmpty)
    {
        List<SlotView> slots = new List<SlotView>();
        if (thing.Slots == null)
        {
            return slots;
        }

        for (int index = 0; index < thing.Slots.Count; index++)
        {
            Slot slot = thing.Slots[index];
            if (slot == null || slot.Type == Slot.Class.Organ)
            {
                continue;
            }

            DynamicThing occupant = slot.Get();
            slots.Add(new SlotView(index, slot.DisplayName, slot.Type.ToString(),
                occupant == null ? null : OccupantOf(occupant, depth - 1, includeEmpty)));
        }

        return slots;
    }

    private static OccupantView OccupantOf(DynamicThing occupant, int depth, bool includeEmpty)
    {
        Item? item = occupant as Item;
        bool hasSlots = occupant.Slots != null && occupant.Slots.Count > 0;
        return new OccupantView(GameLookup.ViewOf(occupant),
            item != null ? WorldItems.QuantityOf(item) : 1,
            item != null ? WorldItems.MaxQuantityOf(item) : null,
            hasSlots && depth > 0 ? SlotsOf(occupant, depth, includeEmpty) : null,
            hasSlots && depth <= 0 ? NotShown(occupant, includeEmpty) : null);
    }

    // The slots past the depth limit a deeper call would show: the occupied ones (organ slots left out, as SlotsOf
    // does), or every one with include_empty; null when none.
    private static int? NotShown(DynamicThing occupant, bool includeEmpty)
    {
        int count = 0;
        foreach (Slot slot in occupant.Slots)
        {
            if (slot != null && slot.Type != Slot.Class.Organ && (includeEmpty || slot.Get() != null))
            {
                count++;
            }
        }

        return count > 0 ? count : null;
    }
}
