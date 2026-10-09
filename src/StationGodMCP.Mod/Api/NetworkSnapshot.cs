#nullable enable

using System;
using System.Collections.Generic;
using Assets.Scripts.Objects.Motherboards;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Profiling;

namespace StationGodMCP.Api;

/// <summary>
/// network_snapshot: the logic values of every device in the scope that matches (prefab_hash, name_contains,
/// reference_ids), up to max_devices by reference id: the listed logic_types, or every type the device reads less the
/// gas and liquid ratios reading 0 (Pure/ZeroRatios; include_zero_ratios keeps them). Read only.
/// </summary>
internal static class NetworkSnapshotApi
{
    private const int MaximumDevices = 256;
    private const int MaximumIds = 256;
    private const int MaximumLogicTypes = 64;

    [Profiled]
    internal static NetworkSnapshotView Handle(Args args)
    {
        DeviceScope scope = Devices.Scope(args);
        int? prefabHash = args.OptionalInt("prefab_hash", int.MinValue, int.MaxValue);
        string? nameContains = args.OptionalString("name_contains");
        int maximum = args.OptionalInt("max_devices", 1, MaximumDevices) ?? ReplyDefaults.SnapshotDevices;
        HashSet<long>? ids = args.Has("reference_ids") ? IdSet(args.ThingIds("reference_ids", MaximumIds)) : null;
        List<LogicType>? requested =
            args.Has("logic_types") ? Types(args.Array("logic_types", MaximumLogicTypes)) : null;
        bool skipZeroRatios = requested == null && !(args.OptionalBool("include_zero_ratios") ?? false);

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
            List<object> values = Values(device, requested ?? LogicTypes.Readable(device), skipZeroRatios,
                out int zeroRatios);
            snapshots.Add(new DeviceSnapshotView(Devices.ViewOf(device, scope), values,
                skipZeroRatios ? zeroRatios : null));
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
