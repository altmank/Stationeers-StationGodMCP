#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure.Paging;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>
/// find device (catalogue find.json): every device with logic in the gateway's scope (DeviceScope: devices, then worn
/// and held logic items), by reference id, kept by prefab and by part of the shown name, then paged (offset, limit);
/// {devices, count, total, offset, limit, has_more, gateway_id}. The first page keeps the matching ids and describes
/// only its own devices; with the wire cursor the rest stays under a cursor (ThingListPage), whose later pages describe
/// each device as it is then, passing over devices gone meanwhile. Read only.
/// </summary>
internal static class FindDevice
{
    private const string List = "devices";

    private static readonly EntryCost Cost = new EntryCost();

    // find device answers no scan counts.
    private static readonly object NoCounts = new object();

    internal static VerbOutcome Run(FindDeviceOptions options)
    {
        Paging paging = options.Paging;
        WalkRequest walk = WalkRequest.Of(paging);
        DeviceScope scope = Devices.Scope(options.Gateway);
        if (walk.Continues)
        {
            return ThingListPage<DeviceView, object>.Later(walk, paging, id => ViewOf(scope, Devices.Find(scope, id)),
                Cost, (page, cursorId, _) => Reply(page, scope, cursorId));
        }

        Selector selector = options.Selector;
        List<ScopedTarget> kept = new List<ScopedTarget>();
        foreach (ScopedTarget device in scope.SortedDevices())
        {
            if ((selector.Prefab == null || selector.Prefab.Keeps(device.PrefabName, device.PrefabHash)) &&
                ItemFilter.Contains(device.DisplayName, selector.Name))
            {
                kept.Add(device);
            }
        }

        long[] ids = new long[kept.Count];
        Dictionary<long, ScopedTarget> byId = new Dictionary<long, ScopedTarget>(kept.Count);
        for (int index = 0; index < kept.Count; index++)
        {
            ids[index] = kept[index].ReferenceId;
            byId[ids[index]] = kept[index];
        }

        return ThingListPage<DeviceView, object>.First(walk, paging, ids, ids.Length, NoCounts,
            id => ViewOf(scope, byId.TryGetValue(id, out ScopedTarget? device) ? device : null), Cost,
            (page, cursorId, _) => Reply(page, scope, cursorId));
    }

    private static DeviceView? ViewOf(DeviceScope scope, ScopedTarget? device) =>
        device != null && !device.Thing.IsBeingDestroyed ? Devices.ViewOf(device, scope) : null;

    private static JObject Reply(Page<DeviceView> page, DeviceScope scope, string? cursorId) =>
        ThingListReply.Of(List, page, cursorId, ("gateway_id", scope.Id));
}
