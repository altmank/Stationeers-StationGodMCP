#nullable enable

using System;
using System.Collections.Generic;
using Assets.Scripts.Objects.Motherboards;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Profiling;
using StationGodMCP.Pure.Scheduling;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// The logic values of every device in the scope that matches, as read snapshot reads them, up to maximum by reference
/// id: the logic types listed, or every type the device reads less the gas and liquid ratios reading 0
/// (Pure/ZeroRatios; keepZeroRatios keeps them). Read only.
/// </summary>
internal static class DeviceSnapshots
{
    // read.json's limit maximum: how many devices one call can return.
    private const int MaximumLimit = 256;

    /// <summary>
    /// The devices kept: by prefab (null: any), part of the shown name, and the ids (null: any); logic the types to
    /// read (null: every readable one). A Work body: the scope's devices gathered over steps (DeviceScope.Scan), a
    /// device matched, then read, per step.
    /// </summary>
    internal static IEnumerable<object?> Reading(DeviceScope scope, Pure.Verbs.PrefabFilter? prefab, string? name,
        HashSet<long>? ids, JArray? logic, bool keepZeroRatios, int maximum, Answer<NetworkSnapshotView> answer)
    {
        List<LogicType>? requested = logic != null ? Types(logic) : null;
        bool skipZeroRatios = requested == null && !keepZeroRatios;

        Dictionary<long, ScopedTarget> map = new Dictionary<long, ScopedTarget>();
        foreach (object? step in scope.Scan(map))
        {
            yield return step;
        }

        List<ScopedTarget> sorted = new List<ScopedTarget>(map.Values);
        sorted.Sort(static (a, b) => a.ReferenceId.CompareTo(b.ReferenceId));
        yield return null;
        List<ScopedTarget> matches = new List<ScopedTarget>();
        foreach (ScopedTarget device in sorted)
        {
            if ((prefab == null || prefab.Keeps(device.PrefabName, device.PrefabHash)) &&
                (ids == null || ids.Contains(device.ReferenceId)) &&
                ItemFilter.Contains(device.DisplayName, name))
            {
                matches.Add(device);
            }

            yield return null;
        }

        List<DeviceSnapshotView> snapshots = new List<DeviceSnapshotView>(Math.Min(maximum, matches.Count));
        for (int index = 0; index < matches.Count && index < maximum; index++)
        {
            ScopedTarget device = matches[index];
            List<object> values = Values(device, requested ?? LogicTypes.Readable(device), skipZeroRatios,
                out int zeroRatios);
            snapshots.Add(new DeviceSnapshotView(Devices.ViewOf(device, scope), values,
                skipZeroRatios ? zeroRatios : null));
            yield return null;
        }

        Pure.Shaping.Truncations.Capped("devices", snapshots.Count, matches.Count, "limit", MaximumLimit);
        answer.Value = new NetworkSnapshotView(scope.Id, snapshots, matches.Count);
    }

    // Each type once, in the order given.
    private static List<LogicType> Types(JArray array)
    {
        List<LogicType> types = new List<LogicType>(array.Count);
        HashSet<ushort> seen = new HashSet<ushort>();
        foreach (JToken token in array)
        {
            LogicType type = LogicTypes.Parse(token);
            if (seen.Add((ushort)type))
            {
                types.Add(type);
            }
        }

        return types;
    }

    // skipZeroRatios: a gas or liquid ratio reading 0 is counted in zeroRatios, not listed (Pure/ZeroRatios).
    private static List<object> Values(ScopedTarget device, List<LogicType> types, bool skipZeroRatios,
        out int zeroRatios)
    {
        List<object> values = new List<object>(types.Count);
        zeroRatios = 0;
        foreach (LogicType type in types)
        {
            LogicTypeView view = LogicTypes.ViewOf(type);
            if (!LogicTypes.CanRead(device, type))
            {
                values.Add(new LogicValueErrorView(view, new ErrorView("logic_not_readable",
                    $"Device does not expose {LogicTypes.Label(type)} as readable.")));
                continue;
            }

            try
            {
                double value = device.GetLogicValue(type);
                if (skipZeroRatios && ZeroRatios.Skips(view.Name, value))
                {
                    zeroRatios++;
                    continue;
                }

                values.Add(new LogicValueView(view, value));
            }
            catch (Exception exception)
            {
                // ILogicable.GetLogicValue can throw for a device mid-change; that value fails alone.
                values.Add(new LogicValueErrorView(view, new ErrorView("read_failed", exception.Message)));
            }
        }

        return values;
    }
}
