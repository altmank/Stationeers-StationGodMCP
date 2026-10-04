#nullable enable

using System;
using System.Collections.Generic;
using Assets.Scripts.Objects.Motherboards;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;

namespace StationGodMCP.Api;

/// <summary>
/// network_snapshot: the logic values of every device in the scope that matches (prefab_hash, name_contains,
/// reference_ids), up to max_devices by reference id: the listed logic_types, or every type the device reads.
/// Read only.
/// </summary>
internal static class NetworkSnapshotApi
{
    private const int MaximumDevices = 256;
    private const int MaximumIds = 256;
    private const int MaximumLogicTypes = 64;

    internal static NetworkSnapshotView Handle(Args args)
    {
        DeviceScope scope = Devices.Scope(args);
        int? prefabHash = args.OptionalInt("prefab_hash", int.MinValue, int.MaxValue);
        string? nameContains = args.OptionalString("name_contains");
        int maximum = args.OptionalInt("max_devices", 1, MaximumDevices) ?? ReplyDefaults.SnapshotDevices;
        HashSet<long>? ids = args.Has("reference_ids") ? IdSet(args.ThingIds("reference_ids", MaximumIds)) : null;
        List<LogicType>? requested =
            args.Has("logic_types") ? Types(args.Array("logic_types", MaximumLogicTypes)) : null;

        List<ScopedTarget> matches = new List<ScopedTarget>();
        foreach (ScopedTarget device in scope.SortedDevices())
        {
            if ((!prefabHash.HasValue || device.PrefabHash == prefabHash.Value) &&
                (ids == null || ids.Contains(device.ReferenceId)) &&
                ItemFilter.Contains(device.DisplayName, nameContains))
            {
                matches.Add(device);
            }
        }

        List<DeviceSnapshotView> snapshots = new List<DeviceSnapshotView>(Math.Min(maximum, matches.Count));
        for (int index = 0; index < matches.Count && index < maximum; index++)
        {
            ScopedTarget device = matches[index];
            snapshots.Add(new DeviceSnapshotView(Devices.ViewOf(device, scope),
                Values(device, requested ?? LogicTypes.Readable(device))));
        }

        Pure.Shaping.Truncations.Capped("devices", snapshots.Count, matches.Count, "max_devices", MaximumDevices);
        return new NetworkSnapshotView(scope.Id, snapshots, matches.Count);
    }

    private static HashSet<long> IdSet(List<ThingId> ids)
    {
        HashSet<long> set = new HashSet<long>();
        foreach (ThingId id in ids)
        {
            set.Add(id.Value);
        }

        return set;
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

    private static List<object> Values(ScopedTarget device, List<LogicType> types)
    {
        List<object> values = new List<object>(types.Count);
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
                values.Add(new LogicValueView(view, device.GetLogicValue(type)));
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
