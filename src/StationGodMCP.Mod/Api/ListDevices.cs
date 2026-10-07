#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure.Profiling;

namespace StationGodMCP.Api;

/// <summary>
/// list_devices: every device in the scope (DeviceScope.DeviceMap: devices, then worn and held logic items), by
/// reference id, optionally only one prefab (prefab_hash) or names containing name_contains. Read only.
/// </summary>
internal static class ListDevicesApi
{
    [Profiled]
    internal static DevicesView Handle(Args args)
    {
        DeviceScope scope = Devices.Scope(args);
        int? prefabHash = args.OptionalInt("prefab_hash", int.MinValue, int.MaxValue);
        string? nameContains = args.OptionalString("name_contains");
        List<DeviceView> devices = new List<DeviceView>();
        foreach (ScopedTarget device in scope.SortedDevices())
        {
            if ((!prefabHash.HasValue || device.PrefabHash == prefabHash.Value) &&
                ItemFilter.Contains(device.DisplayName, nameContains))
            {
                devices.Add(Devices.ViewOf(device, scope));
            }
        }

        return new DevicesView(scope.Id, devices);
    }
}
