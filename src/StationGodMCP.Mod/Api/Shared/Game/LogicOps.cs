#nullable enable

using System;
using System.Collections.Generic;
using Assets.Scripts.Objects.Motherboards;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure.Protocol;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// One logic read or write through the device's own ILogicable (GetLogicValue, SetLogicValue), as a chip's l and s
/// instructions do, and the batch forms that run many in order, each failing alone.
/// </summary>
internal static class LogicOps
{
    internal static LogicRead Read(DeviceScope scope, Args args)
    {
        ScopedTarget device = Devices.Require(scope, args.ThingId("reference_id"));
        LogicType type = LogicTypes.Parse(args.Optional("logic_type"));
        return new LogicRead(new ThingId(device.ReferenceId), LogicTypes.ViewOf(type), ReadValue(device, type));
    }

    /// <summary>One value as read_logic reads it: refused when the device does not expose the type as readable.</summary>
    internal static double ReadValue(ScopedTarget device, LogicType type)
    {
        LogicTypes.RequireReadable(device, type);
        return ValueOf(device, type);
    }

    internal static LogicWrite Write(DeviceScope scope, Args args)
    {
        ScopedTarget device = Devices.Require(scope, args.ThingId("reference_id"));
        LogicType type = LogicTypes.Parse(args.Optional("logic_type"));
        double value = args.Double("value");
        LogicTypes.RequireWritable(device, type);
        double? previous = LogicTypes.CanRead(device, type) ? ValueOf(device, type) : null;
        SetValue(device, type, value);
        double? current = LogicTypes.CanRead(device, type) ? ValueOf(device, type) : null;
        // The calling stream's last written value, for the overwrite check of a restore it holds (HeldRestores).
        StationGodMod.Restores?.Wrote(CallContext.Current, device.ReferenceId, (ushort)type, current ?? value);
        LogicRead requested = new LogicRead(new ThingId(device.ReferenceId), LogicTypes.ViewOf(type), value);
        return new LogicWrite(requested, previous, current);
    }

    /// <summary>
    /// The game write of write_logic and of a held restore: the device's own setter, as a chip's s instruction writes;
    /// refused when the device does not expose the type as writable, write_failed when its setter throws.
    /// </summary>
    internal static void SetValue(ScopedTarget device, LogicType type, double value)
    {
        LogicTypes.RequireWritable(device, type);
        try
        {
            device.SetLogicValue(type, value);
        }
        catch (Exception exception)
        {
            // ILogicable.SetLogicValue: a device's own setter can throw; the write fails with its message.
            throw ApiErrors.Refused("write_failed", exception.Message);
        }
    }

    internal static BatchResultView ReadMany(DeviceScope scope, List<Args?> items)
    {
        BatchBuilder batch = new BatchBuilder(items.Count);
        for (int index = 0; index < items.Count; index++)
        {
            if (TryRead(scope, items[index], index, "reads", out LogicRead? read, out ApiException? error))
            {
                batch.Succeeded(new LogicReadItemView(index, read!));
            }
            else
            {
                batch.Failed(Failure(items[index], index, error!));
            }
        }

        return batch.Build();
    }

    internal static BatchResultView WriteMany(DeviceScope scope, List<Args?> items)
    {
        BatchBuilder batch = new BatchBuilder(items.Count);
        for (int index = 0; index < items.Count; index++)
        {
            if (TryWrite(scope, items[index], index, out LogicWrite? write, out ApiException? error))
            {
                batch.Succeeded(new LogicWriteItemView(index, write!));
            }
            else
            {
                batch.Failed(Failure(items[index], index, error!));
            }
        }

        return batch.Build();
    }

    private static bool TryRead(DeviceScope scope, Args? item, int index, string name, out LogicRead? read,
        out ApiException? error)
    {
        read = null;
        error = item == null ? NotAnObject(name, index) : null;
        if (item == null)
        {
            return false;
        }

        try
        {
            read = Read(scope, item);
            return true;
        }
        catch (ApiException refused)
        {
            error = refused;
            return false;
        }
    }

    private static bool TryWrite(DeviceScope scope, Args? item, int index, out LogicWrite? write,
        out ApiException? error)
    {
        write = null;
        error = item == null ? NotAnObject("writes", index) : null;
        if (item == null)
        {
            return false;
        }

        try
        {
            write = Write(scope, item);
            return true;
        }
        catch (ApiException refused)
        {
            error = refused;
            return false;
        }
    }

    // A failed entry names the device and logic type it asked for, where they can be read from it.
    private static LogicFailedItemView Failure(Args? item, int index, ApiException error)
    {
        ThingId? id = item != null && ThingId.TryRead(item.Optional("reference_id"), out ThingId read)
            ? read
            : (ThingId?)null;
        return new LogicFailedItemView(index, id, item != null ? NamedType(item) : null, error);
    }

    private static LogicTypeView? NamedType(Args item)
    {
        try
        {
            return LogicTypes.ViewOf(LogicTypes.Parse(item.Optional("logic_type")));
        }
        catch (ApiException)
        {
            // The entry's logic_type is missing or not a logic type: its error already says so.
            return null;
        }
    }

    private static ApiException NotAnObject(string name, int index) =>
        ApiErrors.InvalidArgument($"Argument '{Vocabulary.Path(name)}[{index}]' must be an object.");

    private static double ValueOf(ScopedTarget device, LogicType type)
    {
        try
        {
            return device.GetLogicValue(type);
        }
        catch (Exception exception)
        {
            // ILogicable.GetLogicValue: a device's own getter can throw; the read fails with its message.
            throw ApiErrors.Refused("read_failed", exception.Message);
        }
    }
}
