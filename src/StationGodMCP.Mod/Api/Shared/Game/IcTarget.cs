#nullable enable

using System;
using System.Collections.Generic;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Items;
using Assets.Scripts.Objects.Motherboards;
using Assets.Scripts.Objects.Pipes;
using StationGodMCP.Api.Views;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// A circuit holder the call's scope reaches. Target is the scoped thing: an IC Housing, a worn or held item such as a
/// suit or visor, or the device or worn item that has the holder in a slot (the Console or Computer holding a
/// ScriptedScreens Lua board, the tablet holding a Lua cartridge). HolderThing is the ICircuitHolder itself, the same
/// thing as Target unless the holder sits in one of its slots. Kind says what powers the chip.
/// </summary>
internal sealed class IcTarget
{
    private const int PinCount = 6;

    internal IcTarget(ScopedTarget target, Thing holderThing, ICircuitHolder holder)
    {
        Target = target;
        HolderThing = holderThing;
        Holder = holder;
        Housing = holder as CircuitHousing;
        Kind = IcHolderKind.Of(this);
    }

    internal ScopedTarget Target { get; }

    internal Thing HolderThing { get; }

    internal ICircuitHolder Holder { get; }

    /// <summary>Null unless the holder is an IC Housing.</summary>
    internal CircuitHousing? Housing { get; }

    internal IcHolderKind Kind { get; }

    /// <summary>The key pause state is kept under: the holder's reference id.</summary>
    internal long HolderId => HolderThing.ReferenceId;

    internal bool IsOn => Kind.IsOn(this);

    internal bool IsPowered => Kind.IsPowered(this);

    /// <summary>Whether the holder runs its chip now; each IcHolderKind states the game's own test.</summary>
    internal bool IsOperable => Kind.IsOperable(this);

    internal ThingView HolderView => new ThingView(new ThingId(HolderThing.ReferenceId), HolderThing.PrefabName,
        HolderThing.DisplayName);

    internal IcHolderView StateView() => new IcHolderView(Kind.Name, IsOn, IsPowered, IsOperable);

    /// <summary>
    /// The chip in the housing's chip slot (CircuitHousing._ProgrammableChipSlot), or in any slot of another holder;
    /// null when there is none.
    /// </summary>
    internal ProgrammableChip? Chip()
    {
        if (Housing != null)
        {
            Slot? slot = Housing._ProgrammableChipSlot;
            return slot != null ? slot.Get<ProgrammableChip>() : null;
        }

        return FirstOccupant<ProgrammableChip>(HolderThing.Slots);
    }

    internal ProgrammableChip RequireChip()
    {
        ProgrammableChip? chip = Chip();
        if (chip == null)
        {
            throw ApiErrors.Refused("no_programmable_chip",
                $"Circuit holder {HolderThing.ReferenceId} ({HolderThing.PrefabName}) has no programmable chip " +
                "installed.");
        }

        return chip;
    }

    /// <summary>Pins d0..dN as the chip resolves them. Entries are null when nothing is connected.</summary>
    internal IList<ILogicable?> GetPins()
    {
        if (Housing != null)
        {
            return Housing.Devices ?? Array.Empty<ILogicable>();
        }

        List<ILogicable?> pins = new List<ILogicable?>(PinCount);
        for (int index = 0; index < PinCount; index++)
        {
            pins.Add(HolderPin(index));
        }

        return pins;
    }

    /// <summary>The first live occupant of these slots that is a T, or null.</summary>
    internal static T? FirstOccupant<T>(List<Slot>? slots) where T : class
    {
        if (slots == null)
        {
            return null;
        }

        foreach (Slot slot in slots)
        {
            if (slot != null && slot.Get() is T occupant && occupant is UnityEngine.Object alive && alive != null)
            {
                return occupant;
            }
        }

        return null;
    }

    private ILogicable? HolderPin(int index)
    {
        try
        {
            return Holder.IsValidIndex(index) ? Holder.GetLogicableFromIndex(index) : null;
        }
        catch (Exception)
        {
            // ICircuitHolder.GetLogicableFromIndex on an unworn suit, which has no ParentEntity: unconnected.
            return null;
        }
    }
}

/// <summary>
/// What runs a holder's chip, and so what on, powered and operable mean for it. Each kind states the game's (or
/// ScriptedScreens') own test.
/// </summary>
internal abstract class IcHolderKind
{
    private static readonly IcHolderKind HousingHolder = new HousingKind();
    private static readonly IcHolderKind WornHolder = new WornKind();
    private static readonly IcHolderKind BoardHolder = new ComputerBoardKind();
    private static readonly IcHolderKind CartridgeHolder = new ContainedKind("cartridge");
    private static readonly IcHolderKind InsertedHolder = new ContainedKind("inserted_item");

    /// <summary>ic_housing, worn_item, computer_board, cartridge or inserted_item.</summary>
    internal abstract string Name { get; }

    internal static IcHolderKind Of(IcTarget ic)
    {
        if (ic.Housing != null)
        {
            return HousingHolder;
        }

        if (ReferenceEquals(ic.HolderThing, ic.Target.Thing))
        {
            return WornHolder;
        }

        return ic.HolderThing switch
        {
            Motherboard => BoardHolder,
            Cartridge => CartridgeHolder,
            _ => InsertedHolder
        };
    }

    internal abstract bool IsOn(IcTarget ic);

    internal abstract bool IsPowered(IcTarget ic);

    internal abstract bool IsOperable(IcTarget ic);

    /// <summary>An IC Housing: on, powered and operable (CircuitHousing.IsOperable).</summary>
    private sealed class HousingKind : IcHolderKind
    {
        internal override string Name => "ic_housing";

        internal override bool IsOn(IcTarget ic) => ic.Housing!.OnOff;

        internal override bool IsPowered(IcTarget ic) => ic.Housing!.Powered;

        internal override bool IsOperable(IcTarget ic) => ic.Housing!.OnOff && ic.Housing.Powered &&
            (bool)GameMembers.HousingIsOperable.Invoke(ic.Housing)!;
    }

    /// <summary>
    /// A holder the scope reaches directly other than a housing (a suit, a visor, any worn or held holder): a
    /// non-empty battery in one of its slots (SuitBase.Execute and ProgrammableVisorGlasses.Execute run the chip then
    /// and ignore OnOff).
    /// </summary>
    private sealed class WornKind : IcHolderKind
    {
        internal override string Name => "worn_item";

        internal override bool IsOn(IcTarget ic) => ic.Target.Thing.OnOff;

        internal override bool IsPowered(IcTarget ic) => HasChargedBattery(ic.Target.Slots);

        internal override bool IsOperable(IcTarget ic) => HasChargedBattery(ic.Target.Slots);

        private static bool HasChargedBattery(List<Slot>? slots)
        {
            if (slots == null)
            {
                return false;
            }

            foreach (Slot slot in slots)
            {
                if (slot != null && slot.Get() is BatteryCell battery && battery != null && !battery.IsEmpty)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// A board in a Console or Computer: ScriptedScreens runs its chip from the board's power tick while the computer
    /// is on, powered and fully built (ScriptedScreensBehaviorPatch, Item.OnPowerTick postfix).
    /// </summary>
    private sealed class ComputerBoardKind : IcHolderKind
    {
        internal override string Name => "computer_board";

        internal override bool IsOn(IcTarget ic) => ic.Target.Thing.OnOff;

        internal override bool IsPowered(IcTarget ic) => ic.Target.Thing.Powered;

        internal override bool IsOperable(IcTarget ic) => IsOn(ic) && IsPowered(ic) && IsBuilt(ic.Target.Thing);

        private static bool IsBuilt(Thing computer)
        {
            Structure? structure = computer.AsStructure;
            return structure == null || structure.BuildStates == null || structure.BuildStates.Count == 0 ||
                   structure.CurrentBuildStateIndex == structure.BuildStates.Count - 1;
        }
    }

    /// <summary>
    /// A holder in a slot of another scoped thing, such as a Lua cartridge in a tablet: it runs while that thing is on
    /// and powered (CartridgeIntegratedCircuitLua's power tick).
    /// </summary>
    private sealed class ContainedKind : IcHolderKind
    {
        internal ContainedKind(string name)
        {
            Name = name;
        }

        internal override string Name { get; }

        internal override bool IsOn(IcTarget ic) => ic.Target.Thing.OnOff;

        internal override bool IsPowered(IcTarget ic) => ic.Target.Thing.Powered;

        internal override bool IsOperable(IcTarget ic) => IsOn(ic) && IsPowered(ic);
    }
}
