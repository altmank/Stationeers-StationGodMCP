#nullable enable

using System;
using System.Collections.Generic;
using Assets.Scripts.Objects.Motherboards;
using StationGodMCP.Api.Shared.Game.Lint;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure.Lint;
using StationGodMCP.Pure.Protocol;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// One logic read or write through the device's own ILogicable (GetLogicValue, SetLogicValue), as a chip's l and s
/// instructions do, and the batch forms that run many in order, each failing alone. A write is checked (Check), then
/// applied (Apply) or reported.
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

    /// <summary>
    /// One write's checks: the device in scope, the type writable, the value there now, and the write rules when the
    /// write opens the device (OpeningWrites; a problem refuses with lint_refused unless allowLint). They act on
    /// nothing; index names the entry of a list in the warnings.
    /// </summary>
    internal static LogicWritePlan Check(DeviceScope scope, LogicWriteArgs write, bool allowLint, int? index)
    {
        ScopedTarget device = Devices.Require(scope, write.Id);
        LogicTypes.RequireWritable(device, write.Type);
        double? previous = LogicTypes.CanRead(device, write.Type) ? ValueOf(device, write.Type) : null;
        OpeningVerdict verdict = OpeningWrites.Judge(device, write.Type, previous, write.Value);
        LogicRead requested = new LogicRead(new ThingId(device.ReferenceId), LogicTypes.ViewOf(write.Type),
            write.Value);
        return new LogicWritePlan(scope.Id, device, write.Type,
            WriteLint.Checked(requested, previous, verdict, allowLint, index));
    }

    /// <summary>The act of a checked write: the value set, and read back when the type reads.</summary>
    internal static LogicWrite Apply(LogicWritePlan plan)
    {
        try
        {
            plan.Device.SetLogicValue(plan.Type, plan.Checked.RequestedValue);
        }
        catch (Exception exception)
        {
            // ILogicable.SetLogicValue: a device's own setter can throw; the write fails with its message.
            throw ApiErrors.Refused("write_failed", exception.Message);
        }

        double? current = LogicTypes.CanRead(plan.Device, plan.Type) ? ValueOf(plan.Device, plan.Type) : null;
        return new LogicWrite(plan.Checked, current);
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

    /// <summary>
    /// write_logic_many's writes in order, each failing alone: an entry's refusal (lint_refused included) fails that
    /// entry, and the rest still run. Acting, each entry is checked right before its own write, after the writes
    /// before it; report checks each against the world as it is, without the writes before it, and writes nothing.
    /// The entries' warnings, each with its index, go to warnings.
    /// </summary>
    internal static BatchResultView WriteMany(DeviceScope scope, List<Args?> items, bool allowLint, bool report,
        List<BuildIssueView> warnings)
    {
        BatchBuilder batch = new BatchBuilder(items.Count);
        for (int index = 0; index < items.Count; index++)
        {
            if (TryWrite(scope, items[index], index, allowLint, report, warnings, out BatchItemView? done,
                    out ApiException? error))
            {
                batch.Succeeded(done!);
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

    private static bool TryWrite(DeviceScope scope, Args? item, int index, bool allowLint, bool report,
        List<BuildIssueView> warnings, out BatchItemView? done, out ApiException? error)
    {
        done = null;
        error = item == null ? NotAnObject("writes", index) : null;
        if (item == null)
        {
            return false;
        }

        try
        {
            LogicWritePlan plan = Check(scope, LogicWriteArgs.Read(item), allowLint, index);
            done = report
                ? new LogicWritePlannedItemView(index, plan.Checked)
                : (BatchItemView)new LogicWriteItemView(index, Apply(plan));
            warnings.AddRange(plan.Checked.Warnings);
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

/// <summary>One write's arguments, read before its checks: the device, the logic type and the value.</summary>
internal sealed class LogicWriteArgs
{
    private LogicWriteArgs(ThingId id, LogicType type, double value)
    {
        Id = id;
        Type = type;
        Value = value;
    }

    internal ThingId Id { get; }

    internal LogicType Type { get; }

    internal double Value { get; }

    internal static LogicWriteArgs Read(Args args) =>
        new LogicWriteArgs(args.ThingId("reference_id"), LogicTypes.Parse(args.Optional("logic_type")),
            args.Double("value"));
}

/// <summary>
/// A write its checks passed: the scope's gateway id, the device and type it sets, and the checked write the replies
/// show.
/// </summary>
internal sealed class LogicWritePlan
{
    internal LogicWritePlan(string gatewayId, ScopedTarget device, LogicType type, LogicWriteChecked written)
    {
        GatewayId = gatewayId;
        Device = device;
        Type = type;
        Checked = written;
    }

    internal string GatewayId { get; }

    internal ScopedTarget Device { get; }

    internal LogicType Type { get; }

    internal LogicWriteChecked Checked { get; }
}
