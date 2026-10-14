#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>
/// find device (catalogue find.json): every device with logic in the gateway's scope (DeviceScope: devices, then worn
/// and held logic items), by reference id, kept by prefab and by part of the shown name, then paged (offset, limit);
/// {devices, count, total, offset, limit, has_more, gateway_id} (ListReply: count the page's devices, total every
/// device the call found, has_more while more follow). The list pages by offset only. Read only.
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
        JsonSerializer serializer = ApiJson.Fresh();
        return new VerbOutcome.Answered(ListReply.OfPage("devices", page,
            device => JToken.FromObject(device, serializer), ReplyFields.Of(("gateway_id", scope.Id))));
    }
}
