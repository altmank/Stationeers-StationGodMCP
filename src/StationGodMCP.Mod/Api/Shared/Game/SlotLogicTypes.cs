#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using Assets.Scripts.Objects.Motherboards;
using Newtonsoft.Json.Linq;
using StationGodMCP.Pure;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// LogicSlotType by name or number, and the slot logic a device reads at one of its logical slot indexes
/// (ILogicable.CanLogicRead, GetLogicValue with a slot index: what a chip's ls instruction reads).
/// </summary>
internal static class SlotLogicTypes
{
    /// <summary>Every LogicSlotType once, in enum order: the enum has aliases that share a value.</summary>
    internal static readonly LogicSlotType[] Distinct = BuildDistinct();

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

    /// <summary>One enum name (ignoring case) or a number 0..65535, as LogicTypes.Parse reads a logic type.</summary>
    internal static LogicSlotType Parse(JToken token)
    {
        if (token.Type == JTokenType.Integer || token.Type == JTokenType.Float)
        {
            return LogicTypeNumber.TryId((double)token, out ushort number)
                ? (LogicSlotType)number
                : throw ApiErrors.Refused("invalid_logic_type", LogicTypeNumber.RangeMessage);
        }

        string? text = token.Type == JTokenType.String ? token.Value<string>() : token.ToString();
        if (ushort.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out ushort id))
        {
            return (LogicSlotType)id;
        }

        if (EnumName.TryParse(text, out LogicSlotType named))
        {
            return named;
        }

        if (LogicTypeNumber.IsNumberText(text))
        {
            throw ApiErrors.Refused("invalid_logic_type", LogicTypeNumber.RangeMessage);
        }

        throw ApiErrors.Refused("invalid_logic_type",
            $"'{text}' is not a known LogicSlotType name or numeric ushort ID.");
    }

    /// <summary>"Name (id)", or "slot logic type id" for a type with no enum name, for messages.</summary>
    internal static string Label(LogicSlotType type)
    {
        string? name = EnumNames<LogicSlotType>.Of(type);
        return name == null ? $"slot logic type {(ushort)type}" : $"{name} ({(ushort)type})";
    }

    internal static bool CanRead(ScopedTarget device, LogicSlotType type, int index)
    {
        try
        {
            return device.CanLogicRead(type, index);
        }
        catch (Exception)
        {
            // ILogicable.CanLogicRead for a slot type this device's slot does not support.
            return false;
        }
    }

    /// <summary>The value at the slot, refused when the device does not read that type there.</summary>
    internal static double Read(ScopedTarget device, LogicSlotType type, int index)
    {
        if (!CanRead(device, type, index))
        {
            throw ApiErrors.Refused("logic_not_readable",
                $"Device {device.ReferenceId} does not expose {Label(type)} as readable at slot {index}.");
        }

        try
        {
            return device.GetLogicValue(type, index);
        }
        catch (Exception exception)
        {
            // ILogicable.GetLogicValue: a device's own getter can throw; the read fails with its message.
            throw ApiErrors.Refused("read_failed", exception.Message);
        }
    }

    /// <summary>
    /// Every type the device reads at the slot, by enum name, as inspect_slots lists them; a type that does not read
    /// (or throws) is left out.
    /// </summary>
    internal static Dictionary<string, double> ReadAll(ScopedTarget device, int index)
    {
        Dictionary<string, double> values = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (LogicSlotType type in Distinct)
        {
            try
            {
                if (device.CanLogicRead(type, index))
                {
                    values[EnumNames<LogicSlotType>.Of(type) ?? ((ushort)type).ToString(CultureInfo.InvariantCulture)] =
                        device.GetLogicValue(type, index);
                }
            }
            catch (Exception)
            {
                // ILogicable.CanLogicRead or GetLogicValue for a slot type this device's slot does not support.
            }
        }

        return values;
    }

    /// <summary>Refuses a slot index the device does not expose (ILogicable.TotalSlots), as inspect_slots does.</summary>
    internal static void RequireSlot(ScopedTarget device, int index)
    {
        if (index >= Devices.TotalSlots(device))
        {
            throw ApiErrors.Refused("slot_not_found",
                $"Device {device.ReferenceId} does not expose slot index {index}.");
        }
    }
}
