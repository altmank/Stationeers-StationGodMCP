#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using Assets.Scripts.Objects.Motherboards;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// LogicType by name or number, and whether a device reads or writes it. Custom mod logic types work by numeric id
/// even when they have no enum name.
/// </summary>
internal static class LogicTypes
{
    /// <summary>Every LogicType once, in enum order: the enum has aliases that share a value.</summary>
    internal static readonly LogicType[] Distinct = BuildDistinct();

    private static LogicType[] BuildDistinct()
    {
        List<LogicType> types = new List<LogicType>();
        HashSet<ushort> seen = new HashSet<ushort>();
        foreach (LogicType type in Enum.GetValues(typeof(LogicType)))
        {
            if (seen.Add((ushort)type))
            {
                types.Add(type);
            }
        }

        return types.ToArray();
    }

    /// <summary>A LogicType from an enum name (ignoring case) or a number 0..65535, as a string or a number.</summary>
    internal static LogicType Parse(JToken? token)
    {
        if (token == null)
        {
            throw ApiErrors.InvalidArgument("Missing required argument 'logic_type'.");
        }

        if (token.Type == JTokenType.Integer || token.Type == JTokenType.Float)
        {
            // The explicit double conversion also reads an integer past a long (a BigInteger); Value<long> throws.
            return LogicTypeNumber.TryId((double)token, out ushort number)
                ? (LogicType)number
                : throw ApiErrors.Refused("invalid_logic_type", LogicTypeNumber.RangeMessage);
        }

        string? text = token.Type == JTokenType.String ? token.Value<string>() : token.ToString();
        if (ushort.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out ushort id))
        {
            return (LogicType)id;
        }

        if (!string.IsNullOrWhiteSpace(text) && Enum.TryParse(text, true, out LogicType named))
        {
            return named;
        }

        throw ApiErrors.Refused("invalid_logic_type", $"'{text}' is not a known LogicType name or numeric ushort ID.");
    }

    internal static LogicTypeView ViewOf(LogicType type) =>
        new LogicTypeView((ushort)type, Enum.GetName(typeof(LogicType), type));

    /// <summary>"Name (id)", or "logic type id" for a type with no enum name, for messages.</summary>
    internal static string Label(LogicType type)
    {
        string? name = Enum.GetName(typeof(LogicType), type);
        return name == null ? $"logic type {(ushort)type}" : $"{name} ({(ushort)type})";
    }

    internal static bool CanRead(ScopedTarget device, LogicType type)
    {
        try
        {
            return device.CanLogicRead(type);
        }
        catch (Exception)
        {
            // ILogicable.CanLogicRead on a device mid-construction; it reads nothing.
            return false;
        }
    }

    internal static bool CanWrite(ScopedTarget device, LogicType type)
    {
        try
        {
            return device.CanLogicWrite(type);
        }
        catch (Exception)
        {
            // ILogicable.CanLogicWrite on a device mid-construction; it takes nothing.
            return false;
        }
    }

    /// <summary>Refuses a type the device does not expose as readable.</summary>
    internal static void RequireReadable(ScopedTarget device, LogicType type)
    {
        if (!CanRead(device, type))
        {
            throw ApiErrors.Refused("logic_not_readable",
                $"Device {device.ReferenceId} does not expose {Label(type)} as readable.");
        }
    }

    internal static void RequireWritable(ScopedTarget device, LogicType type)
    {
        if (!CanWrite(device, type))
        {
            throw ApiErrors.Refused("logic_not_writable",
                $"Device {device.ReferenceId} does not expose {Label(type)} as writable.");
        }
    }

    /// <summary>Every type the device reads, in enum order.</summary>
    internal static List<LogicType> Readable(ScopedTarget device)
    {
        List<LogicType> types = new List<LogicType>();
        foreach (LogicType type in Distinct)
        {
            if (CanRead(device, type))
            {
                types.Add(type);
            }
        }

        return types;
    }

    /// <summary>The value now, or null when the type does not read.</summary>
    internal static double? ReadIfReadable(ScopedTarget device, LogicType type) =>
        CanRead(device, type) ? device.GetLogicValue(type) : null;
}
