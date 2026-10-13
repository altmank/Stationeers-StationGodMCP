#nullable enable

using System;
using System.Collections.Generic;
using Assets.Scripts.Objects.Motherboards;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared.Game.Lint;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure.Lint;
using StationGodMCP.Pure.Protocol;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// One logic read or write through the device's own ILogicable (GetLogicValue, SetLogicValue), as a chip's l and s
/// instructions do, and the batch forms that run many in order, each failing alone. A write is checked (Check), then
/// applied (Apply) or reported.
/// </summary>
internal static class LogicOps
{
    /// <summary>One logic type of the device in the scope, as read logic reads it.</summary>
    internal static LogicRead Read(DeviceScope scope, ThingId id, JToken logic)
    {
        ScopedTarget device = Devices.Require(scope, id);
        LogicType type = LogicTypes.Parse(logic);
        return new LogicRead(new ThingId(device.ReferenceId), LogicTypes.ViewOf(type), ReadValue(device, type));
    }

    /// <summary>One value as read logic reads it: refused when the device does not expose the type as readable.</summary>
    internal static double ReadValue(ScopedTarget device, LogicType type)
    {
        LogicTypes.RequireReadable(device, type);
        return ValueOf(device, type);
    }

    /// <summary>
    /// One write's checks: the device in scope, the type writable, the value there now, and the write rules when the
    /// write opens the device (OpeningWrites; a problem refuses with lint_refused unless allowLint), with the
    /// controllers that write the same logic type on it (DrivenBy). They act on nothing; index names the entry of a
    /// list in the warnings.
    /// </summary>
    internal static LogicWritePlan Check(DeviceScope scope, ThingId id, LogicType type, double value, bool allowLint,
        int? index)
    {
        ScopedTarget device = Devices.Require(scope, id);
        LogicTypes.RequireWritable(device, type);
        double? previous = LogicTypes.CanRead(device, type) ? ValueOf(device, type) : null;
        OpeningVerdict verdict = OpeningWrites.Judge(device, type, previous, value);
        LogicRead requested = new LogicRead(new ThingId(device.ReferenceId), LogicTypes.ViewOf(type), value);
        return new LogicWritePlan(scope.Id, device, type,
            WriteLint.Checked(requested, previous, verdict, allowLint, index, DrivenBy.For(device, type)));
    }

    /// <summary>
    /// The act of a checked write: the value set (SetValue), and read back when the type reads. The read-back (or the
    /// value asked, when the type does not read) goes to the calling stream's held restores as its last written value.
    /// </summary>
    internal static LogicWrite Apply(LogicWritePlan plan)
    {
        SetValue(plan.Device, plan.Type, plan.Checked.RequestedValue);
        double? current = LogicTypes.CanRead(plan.Device, plan.Type) ? ValueOf(plan.Device, plan.Type) : null;
        // The calling stream's last written value, for the overwrite check of a restore it holds (HeldRestores).
        StationGodMod.Restores?.Wrote(CallContext.Current, plan.Device.ReferenceId, (ushort)plan.Type,
            current ?? plan.Checked.RequestedValue);
        return new LogicWrite(plan.Checked, current);
    }

    /// <summary>
    /// The game write of write logic and of a held restore: the device's own setter, as a chip's s instruction writes;
    /// refused when the device does not expose the type as writable, write_failed when its setter throws. It runs no
    /// write rules and reports to no held restore: its callers judge first, a checked write in Check and a held
    /// restore in HeldRestores (both through OpeningWrites.Judge), and a restore is the mod's own write.
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

    /// <summary>
    /// write logic's writes in order, each failing alone: an entry's refusal (lint_refused included) fails that entry,
    /// and the rest still run. Acting, each entry is checked right before its own write, after the writes before it;
    /// report checks each against the world as it is, without the writes before it, and writes nothing. An id may be
    /// "looking_at". indexed gives each write's warnings its entry's index.
    /// </summary>
    internal static List<LogicWriteResult> WriteMany(DeviceScope scope, IReadOnlyList<LogicOrder> orders,
        bool allowLint, bool report, bool indexed, string label)
    {
        using Controllers.Scope controllers = Controllers.Open();
        List<LogicWriteResult> results = new List<LogicWriteResult>(orders.Count);
        foreach (LogicOrder order in orders)
        {
            try
            {
                LogicWritePlan plan = Check(scope, TargetIds.Of(order.Id, label), LogicTypes.Parse(order.Logic),
                    order.Value, allowLint, indexed ? order.Index : null);
                results.Add(report
                    ? new LogicWriteResult.Planned(order.Index, plan.Device.DisplayName, plan.Checked)
                    : new LogicWriteResult.Written(order.Index, plan.Device.DisplayName, Apply(plan)));
            }
            catch (ApiException refused)
            {
                results.Add(new LogicWriteResult.Failed(order.Index, order.Id, VerbError.Of(refused)));
            }
        }

        return results;
    }

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
