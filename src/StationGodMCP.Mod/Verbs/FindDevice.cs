#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>
/// find device (catalogue find.json): every device with logic in the gateway's scope (DeviceScope: devices, then worn
/// and held logic items), by reference id, kept by prefab and by part of the shown name, then paged (offset, limit);
/// {gateway_id, devices, count}, count the page's devices. The list pages by offset only. Read only.
/// </summary>
internal static class FindDevice
{
    internal static VerbOutcome Run(FindDeviceOptions options)
    {
        if (options.Paging.OffsetOnly() is { } problem)
        {
            return new VerbOutcome.Refused(OptionProblem.Refusal(FindDeviceOptions.Spec.Label, new[] { problem }));
        }

        DeviceScope scope = Devices.Scope(options.Gateway);
        Selector selector = options.Selector;
        List<DeviceView> devices = new List<DeviceView>();
        foreach (ScopedTarget device in scope.SortedDevices())
        {
            if ((selector.Prefab == null || selector.Prefab.Keeps(device.PrefabName, device.PrefabHash)) &&
                ItemFilter.Contains(device.DisplayName, selector.Name))
            {
                devices.Add(Devices.ViewOf(device, scope));
            }
        }

        Page<DeviceView> page = options.Paging.Of(devices);
        return new VerbOutcome.Answered(ReplyFields.Of(("gateway_id", scope.Id), ("devices", page.Entries),
            ("count", page.Entries.Count)));
    }
}
