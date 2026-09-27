#nullable enable

using System.Collections.Generic;
using Assets.Scripts.Objects;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;

namespace StationGodMCP.Api;

/// <summary>
/// container_contents: a thing's slots (Thing.Slots, organ slots left out) and what is in each (Slot.Get), following
/// occupants' own slots down to depth levels. reference_id may be "player" for the local player. Read only.
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
        return new ContainerContentsView(GameLookup.ViewOf(thing), GameLookup.ViewOf(thing.Position),
            origin.DistanceTo(thing.Position), SlotsOf(thing, depth));
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
