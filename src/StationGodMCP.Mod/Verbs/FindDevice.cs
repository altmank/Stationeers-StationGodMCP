#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Facts;
using StationGodMCP.Pure.Paging;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>
/// find device (catalogue find.json): every device with logic in the gateway's scope (DeviceScope: devices, then worn
/// and held logic items), by reference id, kept by prefab, by part of the shown name and by place (near with radius, or
/// box, as find thing: where the device's outermost holder stands), then paged (offset, limit);
/// {devices, count, total, offset, limit, has_more, gateway_id}. The first page keeps the matching ids and describes
/// only its own devices; with the wire cursor the rest stays under a cursor (ThingListPage), whose later pages describe
/// each device as it is then, passing over devices gone meanwhile. Read only.
/// </summary>
[ReplyFact(FactRule.Followed, "which things exist, by class and prefab", Keeper = typeof(StationGodMCP.WorldModel.ThingIndex))]
[ReplyFact(FactRule.Followed, "a gateway's devices", Keeper = typeof(StationGodMCP.Api.Shared.Game.GatewayMaps))]
[ReplyFact(FactRule.Live, "names, prefabs, slots, worn and held items")]
internal static class FindDevice
{
    private const string List = "devices";

    private const double MaximumRadiusM = 1000.0;


    // find device answers no scan counts.
    private static readonly object NoCounts = new object();

    internal static VerbOutcome Run(FindDeviceOptions options)
    {
        Paging paging = options.Paging;
        WalkRequest walk = WalkRequest.Of(paging);
        DeviceScope scope = Devices.Scope(options.Gateway);
        if (walk.Continues)
        {
            return ThingListPage<DeviceView, object>.Later(walk, paging, id => ViewOf(scope, Devices.Find(scope, id)), (page, cursorId, _) => Reply(page, scope, cursorId));
        }

        Selector selector = options.Selector;
        List<OptionProblem> problems = selector.AreaProblems(MaximumRadiusM);
        if (problems.Count > 0)
        {
            return new VerbOutcome.Refused(OptionProblem.Refusal(FindDeviceOptions.Spec.Label, problems));
        }

        PointArea area = selector.Near is { } near && selector.Radius is { } radius
            ? PointArea.Near(NearCentres.Of(near, FindDeviceOptions.Spec.Label), radius)
            : selector.AreaAround(null);
        Dictionary<long, ScopedTarget> devices = new Dictionary<long, ScopedTarget>();
        return ThingListPage<DeviceView, object>.Scan(walk, paging,
            new ScanBuild(keys => Kept(scope, selector, area, devices, keys), ListOrder.ReferenceId), static () => NoCounts,
            id => ViewOf(scope, devices.TryGetValue(id, out ScopedTarget? device) ? device : null),
            (page, cursorId, _) => Reply(page, scope, cursorId));
    }

    // The scope's devices (DeviceScope.Scan), then each one the prefab, name and area filters keep keyed by id, one per
    // step.
    private static IEnumerable<object?> Kept(DeviceScope scope, Selector selector, PointArea area,
        Dictionary<long, ScopedTarget> devices, ScanKeys keys)
    {
        foreach (object? step in scope.Scan(devices))
        {
            yield return step;
        }

        foreach (ScopedTarget device in devices.Values)
        {
            keys.Candidates++;
            if ((selector.Prefab == null || selector.Prefab.Keeps(device.PrefabName, device.PrefabHash)) &&
                ItemFilter.Contains(device.DisplayName, selector.Name) && Inside(area, device))
            {
                keys.Add(new ListKey(null, null, 0, device.ReferenceId));
            }

            yield return null;
        }
    }

    // Where the device stands: its outermost holder's place (a worn or held logic item is where its wearer is).
    private static bool Inside(PointArea area, ScopedTarget device)
    {
        if (area == PointArea.Anywhere)
        {
            return true;
        }

        UnityEngine.Vector3 position = HolderChain.PlaceOf(device.Thing).Position;
        return area.Contains(new Vec3(position.x, position.y, position.z));
    }

    private static DeviceView? ViewOf(DeviceScope scope, ScopedTarget? device) =>
        device != null && !device.Thing.IsBeingDestroyed ? Devices.ViewOf(device, scope) : null;

    private static ReplyObject Reply(Page<DeviceView> page, DeviceScope scope, string? cursorId) =>
        ThingListReply.Of(List, page, cursorId, ("gateway_id", scope.Id));
}
