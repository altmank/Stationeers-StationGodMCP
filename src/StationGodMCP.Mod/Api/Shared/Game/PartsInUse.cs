#nullable enable

using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Items;
using Assets.Scripts.Objects.Pipes;
using Objects.RoboticArm;
using StationGodMCP.Pure;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// Pure/PartInUseRule over the game's slots: which part an item is to the device whose slot holds it, and the in_use
/// refusal every tool that takes items out of slots asks before it does (move_item, silo_deposit; build payments skip
/// such parts). Deconstructing the device itself is not a take: its contents go with it.
/// </summary>
internal static class PartsInUse
{
    /// <summary>The in_use refusal for taking this item out of its slot, or null when its holder is not using it.</summary>
    internal static ApiException? Refusal(DynamicThing item, bool allowInUse)
    {
        Slot? slot = item.ParentSlot;
        if (slot?.Parent == null)
        {
            return null;
        }

        DevicePart part = PartOf(slot);
        bool switchedOn = SwitchedOn(slot.Parent);
        return PartInUseRule.Refuses(part, switchedOn, allowInUse)
            ? ApiErrors.Refused("in_use",
                PartInUseRule.Refusal(Names.Of(item), item.ReferenceId, part, HolderOf(slot, switchedOn)))
            : null;
    }

    /// <summary>Whether the holder of this slot is using what is in it now.</summary>
    internal static bool InUse(Slot slot) =>
        slot.Parent != null && PartInUseRule.InUse(PartOf(slot), SwitchedOn(slot.Parent));

    private static DevicePart PartOf(Slot slot) => PartInUseRule.PartOf(ClassOf(slot.Type), TraitsOf(slot.Parent));

    private static PartSlotClass ClassOf(Slot.Class type) =>
        type switch
        {
            Slot.Class.ProgrammableChip => PartSlotClass.ProgrammableChip,
            Slot.Class.Motherboard => PartSlotClass.Motherboard,
            Slot.Class.GasFilter => PartSlotClass.GasFilter,
            Slot.Class.Battery => PartSlotClass.Battery,
            Slot.Class.GasCanister or Slot.Class.LiquidCanister => PartSlotClass.Canister,
            _ => PartSlotClass.Other
        };

    private static PartHolderTraits TraitsOf(Thing holder)
    {
        PartHolderTraits traits = PartHolderTraits.None;
        if (holder is ICircuitHolder)
        {
            traits |= PartHolderTraits.CircuitHolder;
        }

        if (holder is IComputer)
        {
            traits |= PartHolderTraits.Computer;
        }

        if (holder is FiltrationMachineBase or IndustrialFiltration or RoboticArmDockAtmos)
        {
            traits |= PartHolderTraits.FilterMachine;
        }

        // Carried gear (a suit, tool, helmet or jetpack is an Item) is swapped by hand; a battery charger is neither.
        if (holder is AreaPowerControl || (holder is IBatteryPowered && !(holder is Item)))
        {
            traits |= PartHolderTraits.BatteryMachine;
        }

        if (holder is StirlingEngine or DynamicGenerator or DynamicComposter or DynamicAirConditioner
            or DynamicHydroponics)
        {
            traits |= PartHolderTraits.CanisterMachine;
        }

        return traits;
    }

    // Thing.OnOff reads false for a thing with no on/off switch, which is never switched off.
    private static bool SwitchedOn(Thing holder) => !holder.HasOnOffState || holder.OnOff;

    private static PartHolder HolderOf(Slot slot, bool switchedOn) =>
        new PartHolder(Labels.GameNameOf(slot.Parent), Labels.CustomNameOf(slot.Parent), slot.Parent.ReferenceId,
            slot.SlotIndex, switchedOn);
}
