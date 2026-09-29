#nullable enable

using System.Collections.Generic;
using System.Globalization;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// Finds the circuit holder an IC tool's reference_id names. Three kinds of id work: a scoped device or worn item that
/// is a holder (IC Housing, suit, Programmable Visor), or holds exactly one holder in its own slots (a Console or
/// Computer with a ScriptedScreens Lua board, a tablet with one Lua cartridge); the holder itself (the board, the
/// cartridge); and the chip in a holder. A holder that is not itself in the scope counts when a scoped device or worn
/// item contains it, directly or through slots within slots.
/// </summary>
internal static class CircuitHolders
{
    // Slot nesting between a holder and the thing the scope reaches: a chip in a board in a console is one level.
    private const int MaximumNesting = 8;

    internal static IcTarget Require(DeviceScope scope, ThingId id)
    {
        ScopedTarget? scoped = Devices.Find(scope, id.Value);
        return scoped != null ? HolderOn(scoped) : HolderWithin(scope, id);
    }

    private static IcTarget HolderOn(ScopedTarget scoped)
    {
        if (scoped.Thing is ICircuitHolder holder)
        {
            return new IcTarget(scoped, scoped.Thing, holder);
        }

        List<Thing> held = SlottedHolders(scoped.Thing);
        if (held.Count == 1)
        {
            return new IcTarget(scoped, held[0], (ICircuitHolder)held[0]);
        }

        if (held.Count > 1)
        {
            throw ApiErrors.Refused("ambiguous_circuit_holder",
                $"Device {scoped.ReferenceId} ({scoped.PrefabName}) holds {held.Count} circuit holders " +
                $"({Ids(held)}); pass the reference id of the one you mean.");
        }

        throw ApiErrors.Refused("not_ic_housing",
            $"Device {scoped.ReferenceId} ({scoped.PrefabName}) is not a circuit holder and has " +
            "none in its slots (an IC Housing, suit, Programmable Visor, a Console or Computer with a Lua board, a " +
            "tablet with a Lua cartridge).");
    }

    private static IcTarget HolderWithin(DeviceScope scope, ThingId id)
    {
        Thing? thing = Thing.Find(id.Value);
        if (thing == null)
        {
            throw ApiErrors.Refused("device_not_found",
                $"Device {id} is not visible {scope.Where} and is not worn or held by a player.");
        }

        Thing? holderThing = thing is ProgrammableChip chip ? chip.ParentSlot?.Parent : thing;
        if (!(holderThing is ICircuitHolder holder))
        {
            throw ApiErrors.Refused("not_ic_housing", thing is ProgrammableChip
                ? $"Chip {id} is not in a circuit holder."
                : $"Thing {id} ({thing.PrefabName}) is not a device in scope, a circuit holder or a chip.");
        }

        ScopedTarget? container = ScopedContainer(scope, holderThing);
        if (container == null)
        {
            throw ApiErrors.Refused("device_not_found",
                $"Circuit holder {holderThing.ReferenceId} ({holderThing.PrefabName}) is not inside a device visible " +
                $"{scope.Where} or an item worn or held by a player.");
        }

        return new IcTarget(container, holderThing, holder);
    }

    // The holder itself when the scope reaches it, else the nearest thing holding it (ParentSlot.Parent, outwards)
    // that the scope reaches.
    private static ScopedTarget? ScopedContainer(DeviceScope scope, Thing holder)
    {
        Thing? current = holder;
        for (int depth = 0; current != null && depth <= MaximumNesting; depth++)
        {
            ScopedTarget? scoped = Devices.Find(scope, current.ReferenceId);
            if (scoped != null)
            {
                return scoped;
            }

            current = current is DynamicThing dynamic ? dynamic.ParentSlot?.Parent : null;
        }

        return null;
    }

    // Occupants of the thing's own slots that are circuit holders (a console's board, a tablet's cartridges).
    private static List<Thing> SlottedHolders(Thing thing)
    {
        List<Thing> holders = new List<Thing>();
        if (thing.Slots == null)
        {
            return holders;
        }

        foreach (Slot slot in thing.Slots)
        {
            if (slot != null && slot.Get() is Thing occupant && occupant != null && occupant is ICircuitHolder)
            {
                holders.Add(occupant);
            }
        }

        return holders;
    }

    private static string Ids(List<Thing> things)
    {
        List<string> ids = new List<string>(things.Count);
        foreach (Thing thing in things)
        {
            ids.Add(thing.ReferenceId.ToString(CultureInfo.InvariantCulture) + " " + thing.PrefabName);
        }

        return string.Join(", ", ids);
    }
}
