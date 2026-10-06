#nullable enable

using System.Collections.Generic;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Chutes;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;
using UnityEngine;

namespace StationGodMCP.Api;

/// <summary>
/// container_contents: a thing's slots (Thing.Slots, organ slots left out) and what is in each (Slot.Get), following
/// occupants' own slots down to depth levels. reference_id may be "player" for the player. On an SDB Silo also its
/// store (Silos), whose entries are save data rather than things. Read only.
/// </summary>
internal static class ContainerContentsApi
{
    private const int DefaultDepth = 3;
    private const int MaximumDepth = 6;

    internal static ContainerContentsView Handle(Args args)
    {
        PlayerOrigin origin = PlayerOrigin.Current();
        Thing thing = args.IsWord("reference_id", "player") ? PlayerOrigin.RequireHuman() : Require(args);
        int depth = args.OptionalInt("depth", 1, MaximumDepth) ?? DefaultDepth;
        Vector3 position = HolderChain.PlaceOf(thing).Position;
        SlotFilter filter = new SlotFilter(args.OptionalString("prefab_contains"), args.OptionalString("name_contains"));
        return new ContainerContentsView(GameLookup.ViewOf(thing), GameLookup.ViewOf(position),
            origin.DistanceTo(position), filter.Apply(SlotsOf(thing, depth)),
            thing is Silo silo ? SiloContents(silo, args, filter) : null);
    }

    // A silo's store, after its own slots: entries front first (filtered as the slots are), one page of them. On a
    // client the store is unknown (the host keeps it) and only the synced count is shown.
    private static SiloContentsView SiloContents(Silo silo, Args args, SlotFilter filter)
    {
        PageRequest page = SiloEntriesPage.Of(args);
        if (!Silos.StoresKnown)
        {
            return new SiloContentsView(silo.TotalItemsCurrentlyStored, false, null,
                Slice<SiloEntryView>.Page(new List<SiloEntryView>(), page, 0));
        }

        SiloStore store = SiloStore.Of(silo);
        List<SiloEntryView> entries = new List<SiloEntryView>();
        foreach (SiloEntry entry in store.Describe())
        {
            SiloEntryView view = entry.View();
            if (filter.Keeps(view))
            {
                entries.Add(view);
            }
        }

        Slice<SiloEntryView> slice = Slice<SiloEntryView>.Of(entries, page);
        SiloEntriesPage.Note(page, slice.Items.Count, entries.Count);
        return new SiloContentsView(store.Entries.Count, true, store.Busy(), slice);
    }

    private static Thing Require(Args args)
    {
        ThingId id = args.ThingId("reference_id");
        if (!GameLookup.TryFindThing(id, out Thing thing))
        {
            throw ApiErrors.Refused(ApiErrors.ThingNotFoundCode,
                $"Nothing with reference id {id} exists in the world.");
        }

        return thing;
    }

    private static List<SlotView> SlotsOf(Thing thing, int depth)
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
                occupant == null ? null : OccupantOf(occupant, depth - 1)));
        }

        return slots;
    }

    private static OccupantView OccupantOf(DynamicThing occupant, int depth)
    {
        Item? item = occupant as Item;
        bool hasSlots = occupant.Slots != null && occupant.Slots.Count > 0;
        return new OccupantView(GameLookup.ViewOf(occupant),
            item != null ? WorldItems.QuantityOf(item) : 1,
            item != null ? WorldItems.MaxQuantityOf(item) : null,
            hasSlots && depth > 0 ? SlotsOf(occupant, depth) : null,
            hasSlots && depth <= 0 ? occupant.Slots!.Count : null);
    }
}
