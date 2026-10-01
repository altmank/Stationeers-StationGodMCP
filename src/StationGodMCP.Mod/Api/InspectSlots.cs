#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Motherboards;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;

namespace StationGodMCP.Api;

/// <summary>
/// inspect_slots: a device's logical slots (ILogicable.TotalSlots, GetSlot: the indexes slot logic uses), what is in
/// each, and every LogicSlotType it reads there (ILogicable.CanLogicRead, GetLogicValue). One slot with slot_index.
/// Read only.
/// </summary>
internal static class InspectSlotsApi
{
    private static readonly LogicSlotType[] DistinctSlotTypes = BuildDistinct();

    // LogicTypeView is immutable: one per slot logic type, made on first use.
    private static readonly ConcurrentDictionary<LogicSlotType, LogicTypeView> SlotTypeViews =
        new ConcurrentDictionary<LogicSlotType, LogicTypeView>();

    private static readonly Func<LogicSlotType, LogicTypeView> MakeSlotTypeView =
        static type => new LogicTypeView((ushort)type, EnumNames<LogicSlotType>.Of(type));

    internal static InspectSlotsView Handle(Args args)
    {
        DeviceScope scope = Devices.Scope(args);
        ScopedTarget device = Devices.Require(scope, args.ThingId("reference_id"));
        int? requested = args.OptionalInt("slot_index", 0, int.MaxValue);
        int total = Devices.TotalSlots(device);
        if (requested.HasValue && requested.Value >= total)
        {
            throw ApiErrors.Refused("slot_not_found",
                $"Device {device.ReferenceId} does not expose slot index {requested.Value}.");
        }

        int first = requested ?? 0;
        int end = requested.HasValue ? first + 1 : total;
        List<SlotDetailView> slots = new List<SlotDetailView>(end - first);
        for (int index = first; index < end; index++)
        {
            slots.Add(Describe(device, LogicalSlot(device, index), index));
        }

        return new InspectSlotsView(scope.Id, Devices.ViewOf(device, scope), slots, total);
    }

    private static LogicSlotType[] BuildDistinct()
    {
        List<LogicSlotType> types = new List<LogicSlotType>();
        HashSet<ushort> seen = new HashSet<ushort>();
        foreach (LogicSlotType type in Enum.GetValues(typeof(LogicSlotType)))
        {
            if (seen.Add((ushort)type))
            {
                types.Add(type);
            }
        }

        return types.ToArray();
    }

    private static Slot? LogicalSlot(ScopedTarget device, int index)
    {
        try
        {
            return device.GetSlot(index);
        }
        catch (Exception)
        {
            // ILogicable.GetSlot on an index the device maps to no slot.
            return null;
        }
    }

    private static SlotDetailView Describe(ScopedTarget device, Slot? slot, int index)
    {
        DynamicThing? occupant = slot?.Get();
        SlotFacts? facts = slot == null
            ? null
            : new SlotFacts(slot.SlotIndex, slot.DisplayName, slot.StringKey,
                new EnumValueView((int)slot.Type, EnumNames<Slot.Class>.Of(slot.Type)),
                new SlotFlags(slot.IsInteractable, slot.IsLocked, slot.IsSwappable, slot.HidesOccupant,
                    slot.SpecificTypePrefabHashes));
        SlotOccupantView? occupantView = occupant == null
            ? null
            : new SlotOccupantView(GameLookup.ViewOf(occupant), occupant.PrefabHash, occupant.GetType().FullName);
        return new SlotDetailView(index, facts, occupantView, SlotLogic(device, index));
    }

    // Slot logic support is device- and logical-index-specific: a type that does not read is left out.
    private static List<SlotLogicView> SlotLogic(ScopedTarget device, int index)
    {
        List<SlotLogicView> values = new List<SlotLogicView>();
        foreach (LogicSlotType type in DistinctSlotTypes)
        {
            try
            {
                if (device.CanLogicRead(type, index))
                {
                    LogicTypeView name = SlotTypeViews.GetOrAdd(type, MakeSlotTypeView);
                    values.Add(new SlotLogicView(name, device.GetLogicValue(type, index)));
                }
            }
            catch (Exception)
            {
                // ILogicable.CanLogicRead or GetLogicValue for a slot type this device's slot does not support.
            }
        }

        return values;
    }
}
