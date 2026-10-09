#nullable enable

using System;

namespace StationGodMCP.Pure;

/// <summary>The slot classes (Slot.Class, Slot.cs:22) whose occupant a device can be using as one of its parts.</summary>
internal enum PartSlotClass
{
    Other,
    ProgrammableChip,
    Motherboard,
    GasFilter,
    Battery,
    Canister
}

/// <summary>What the thing holding a slot is, as far as using the part in it goes.</summary>
[Flags]
internal enum PartHolderTraits
{
    None = 0,

    /// <summary>An ICircuitHolder: it runs the chip in its ProgrammableChip slot.</summary>
    CircuitHolder = 1,

    /// <summary>An IComputer (a computer, console or laptop): it works through the motherboard in its slot.</summary>
    Computer = 2,

    /// <summary>A filtration machine or atmos dock: it filters through the filters in its GasFilter slots.</summary>
    FilterMachine = 4,

    /// <summary>A placed machine running off the battery cell in its battery slot (not a battery charger).</summary>
    BatteryMachine = 8,

    /// <summary>A machine that draws on the canister in its canister slot (not a canister dock or storage).</summary>
    CanisterMachine = 16
}

/// <summary>A part a device uses, by what it does there.</summary>
internal enum DevicePart
{
    None,
    Chip,
    Motherboard,
    Filter,
    Battery,
    Canister
}

/// <summary>The device a part is in, as the refusal names it.</summary>
internal sealed class PartHolder
{
    internal PartHolder(string kind, string? label, long referenceId, int slotIndex, bool switchedOn)
    {
        Kind = kind;
        Label = string.IsNullOrEmpty(label) ? null : label;
        ReferenceId = referenceId;
        SlotIndex = slotIndex;
        SwitchedOn = switchedOn;
    }

    /// <summary>The device's own name (its prefab's shown name), without its label.</summary>
    internal string Kind { get; }

    /// <summary>The label a player gave it, or null.</summary>
    internal string? Label { get; }

    internal long ReferenceId { get; }

    internal int SlotIndex { get; }

    /// <summary>Switched on, or with no on/off switch at all (Thing.OnOff is false without HasOnOffState).</summary>
    internal bool SwitchedOn { get; }

    internal string Named =>
        Label == null ? $"{Kind} {ReferenceId}" : $"{Kind} \"{Label}\" ({ReferenceId})";
}

/// <summary>
/// Which items a device is using, so no tool takes one out of it unasked (allow_in_use). Two kinds of part:
///
/// Program parts are in use whenever they sit in their holder, switched on or not, because the device's working
/// state lives in them. A programmable chip in any ICircuitHolder (IC housing, a circuit device's chip slot, laptop,
/// robot, suit, tablet): CircuitHousing runs it while on and powered (CircuitHousing.cs:321), and taking it out resets
/// it (OnChildExitInventory, CircuitHousing.cs:594-600). A motherboard in a computer or console: Computer.
/// OnChildEnterInventory makes it CurrentMotherboard (Computer.cs:279-285), and leaving clears it (Computer.cs:325).
///
/// Running parts are in use while the device is switched on: filters in a filtration machine or atmos dock
/// (FiltrationMachineBase.cs:102, 191; IndustrialFiltration.cs:228; RoboticArmDockAtmos.cs:75, 415), the battery cell
/// of a placed battery-run machine (AreaPowerControl.cs:60, WallLightBattery.cs:21, RobotMining.cs:211, Rover.cs:219),
/// and the canister of a machine that draws on it (StirlingEngine.cs:120, DynamicGenerator.cs:105-108,
/// DynamicComposter.cs:386-392, DynamicAirConditioner.cs:51-55, DynamicHydroponics.cs:53). A switched-off device is
/// not using them, so a player's swap of a spent filter or flat battery goes through once it is off.
///
/// Left out on purpose: battery chargers and canister docks (BatteryCellCharger, GasTankStorage, SuitStorage), whose
/// job is to hand the part back; carried gear's batteries, tanks and filters (suits, tools, helmets, jetpacks), which
/// the player swaps by hand; and any slot of another class (a storage slot, an import or export slot).
/// </summary>
internal static class PartInUseRule
{
    /// <summary>The part an occupant of this slot class is to a holder with these traits.</summary>
    internal static DevicePart PartOf(PartSlotClass slot, PartHolderTraits holder) =>
        slot switch
        {
            PartSlotClass.ProgrammableChip when Has(holder, PartHolderTraits.CircuitHolder) => DevicePart.Chip,
            PartSlotClass.Motherboard when Has(holder, PartHolderTraits.Computer) => DevicePart.Motherboard,
            PartSlotClass.GasFilter when Has(holder, PartHolderTraits.FilterMachine) => DevicePart.Filter,
            PartSlotClass.Battery when Has(holder, PartHolderTraits.BatteryMachine) => DevicePart.Battery,
            PartSlotClass.Canister when Has(holder, PartHolderTraits.CanisterMachine) => DevicePart.Canister,
            _ => DevicePart.None
        };

    /// <summary>Whether the device is using the part now.</summary>
    internal static bool InUse(DevicePart part, bool switchedOn) =>
        part switch
        {
            DevicePart.None => false,
            DevicePart.Chip or DevicePart.Motherboard => true,
            _ => switchedOn
        };

    /// <summary>Whether a take must be refused: the part is in use and the caller did not pass allow_in_use.</summary>
    internal static bool Refuses(DevicePart part, bool switchedOn, bool allowInUse) =>
        !allowInUse && InUse(part, switchedOn);

    /// <summary>The in_use refusal: the item, the device with its label, the slot, and what taking it out does.</summary>
    internal static string Refusal(string item, long itemId, DevicePart part, PartHolder holder) =>
        $"{item} ({itemId}) is in slot {holder.SlotIndex} of {holder.Named}, which is using it: {Consequence(part)}. "
        + "Take a spare instead, or pass allow_in_use true to take it anyway.";

    private static string Consequence(DevicePart part) =>
        part switch
        {
            DevicePart.Chip => "it runs its program from this chip, and taking it out resets the chip and stops the "
                               + "program",
            DevicePart.Motherboard => "it works through this motherboard, and taking it out leaves it without one",
            DevicePart.Filter => "it is switched on and filters through this filter",
            DevicePart.Battery => "it is switched on and runs off this battery",
            DevicePart.Canister => "it is switched on and draws on this canister",
            _ => "it uses it"
        };

    private static bool Has(PartHolderTraits holder, PartHolderTraits trait) => (holder & trait) == trait;
}
