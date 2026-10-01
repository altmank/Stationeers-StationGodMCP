#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Pipes;
using Assets.Scripts.Objects.Motherboards;
using StationGodMCP.Pure.Lint;

namespace StationGodMCP.Api.Shared.Game.Lint;

/// <summary>
/// The program chips the lint model sees: a circuit holder (an IC Housing, a Lua circuit board) on a data network, or
/// one held in a device's slots (a Console's Lua board). Its program is ICircuitHolder.GetSourceCode; Lua when its chip
/// is a Lua chip (LuaChips.IsLua); its pins are an IC Housing's Devices.
/// </summary>
internal static class LintChips
{
    internal static List<LintRecord> OnNetwork(GameLintWorld world, CableNetwork network)
    {
        List<LintRecord> chips = new List<LintRecord>();
        List<Device> devices;
        try
        {
            devices = new List<Device>(network.DataDeviceList);
        }
        catch (InvalidOperationException)
        {
            return chips;
        }

        foreach (Device device in devices)
        {
            if (device == null || device.IsBeingDestroyed)
            {
                continue;
            }

            foreach (Thing holder in HoldersOn(device))
            {
                LintRecord? chip = Record(world, holder, device);
                if (chip != null)
                {
                    chips.Add(chip);
                }
            }
        }

        return chips;
    }

    /// <summary>The chip a thing holds: itself as a circuit holder, or the one circuit holder in its slots.</summary>
    internal static LintRecord? ChipOf(GameLintWorld world, Thing thing)
    {
        List<Thing> holders = HoldersOn(thing);
        return holders.Count > 0 ? Record(world, holders[0], thing) : null;
    }

    private static List<Thing> HoldersOn(Thing thing)
    {
        List<Thing> holders = new List<Thing>();
        if (thing is ICircuitHolder)
        {
            holders.Add(thing);
            return holders;
        }

        if (thing.Slots == null)
        {
            return holders;
        }

        foreach (Slot slot in thing.Slots)
        {
            if (slot?.Get() is Thing occupant && occupant != null && occupant is ICircuitHolder)
            {
                holders.Add(occupant);
            }
        }

        return holders;
    }

    private static LintRecord? Record(GameLintWorld world, Thing holderThing, Thing owner)
    {
        ICircuitHolder holder = (ICircuitHolder)holderThing;
        string? source;
        try
        {
            source = holder.GetSourceCode();
        }
        catch (Exception error) when (error is InvalidOperationException || error is NullReferenceException)
        {
            return null;
        }

        if (string.IsNullOrEmpty(source))
        {
            return null;
        }

        ProgrammableChip? chip = ChipIn(holderThing);
        List<LintValue> pins = new List<LintValue>();
        if (holderThing is CircuitHousing housing && housing.Devices != null)
        {
            foreach (ILogicable? device in housing.Devices)
            {
                pins.Add(LintValue.Of(device is Thing pinned ? world.Thing(pinned) : null));
            }
        }

        string language = chip != null ? LuaChips.IsLua(chip) ? "lua" : "ic10" : "lua";
        Dictionary<string, LintValue> values = new Dictionary<string, LintValue>(StringComparer.Ordinal)
        {
            ["housing"] = LintValue.Of(world.Thing(owner) ?? world.Thing(holderThing)),
            ["language"] = LintValue.Of(language),
            ["source"] = LintValue.Of(source),
            ["pins"] = LintValue.Of(pins)
        };
        return new LintRecord(LintModel.Chip, "chip:" + holderThing.ReferenceId.ToString(CultureInfo.InvariantCulture),
            values, describe: $"chip in {holderThing.PrefabName} {holderThing.ReferenceId}");
    }

    // The programmable chip a holder runs: the chip in an IC Housing's slot, or the holder itself (a Lua board).
    private static ProgrammableChip? ChipIn(Thing holder)
    {
        if (holder is ProgrammableChip own)
        {
            return own;
        }

        if (holder.Slots != null)
        {
            foreach (Slot slot in holder.Slots)
            {
                if (slot?.Get() is ProgrammableChip chip && chip != null)
                {
                    return chip;
                }
            }
        }

        return null;
    }
}
