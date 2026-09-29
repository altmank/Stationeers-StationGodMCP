#nullable enable

using System;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Motherboards;
using Assets.Scripts.Objects.Pipes;
using Assets.Scripts.Util;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Views;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>The device layer every device and IC tool shares: the scope, the device, and how a device reads.</summary>
internal static class Devices
{
    /// <summary>
    /// The scope a device or IC call works in: the whole world when gateway_id is omitted, blank or "world", or, as
    /// a filter, the devices on one StationGod Gateway's data networks.
    /// </summary>
    internal static DeviceScope Scope(Args args)
    {
        JToken? token = args.Optional("gateway_id");
        string? text = token != null && token.Type == JTokenType.String ? token.Value<string>()!.Trim() : null;
        if (token == null || text == string.Empty ||
            string.Equals(text, DeviceScope.WorldId, StringComparison.OrdinalIgnoreCase))
        {
            return DeviceScope.World;
        }

        ThingId id = args.ThingId("gateway_id");
        if (!GatewayRegistry.TryGet(id.Value, out StationGodGateway? gateway))
        {
            throw ApiErrors.Refused("gateway_not_found", $"No StationGod Gateway has reference ID {id}.");
        }

        string? unavailable = gateway!.GetUnavailableReason();
        if (unavailable != null)
        {
            throw ApiErrors.Refused("gateway_unavailable", $"Gateway {id} is unavailable: {unavailable}.");
        }

        return DeviceScope.ForGateway(gateway);
    }

    /// <summary>
    /// One scoped target by reference id, the same one DeviceScope.DeviceMap would hold, found through the game's
    /// reference lookup (Thing.Find) instead of a map of every device: in the world scope a Device in
    /// Device.AllDevices, in a gateway scope a device on the gateway's data networks, and in either a worn or held
    /// logic item when no device has that id.
    /// </summary>
    internal static ScopedTarget Require(DeviceScope scope, ThingId id) =>
        Find(scope, id.Value) ?? throw NotFound(scope, id);

    // A thing that exists but has no logic (a crate, a lander, a tool) is named as such, not as out of sight, and so is
    // a logic item (a labeller, a tablet) that no player wears or holds, and a structure with logic that is not a
    // Device (a landing pad centre): neither is in Device.AllDevices.
    private static ApiException NotFound(DeviceScope scope, ThingId id) =>
        Thing.Find(id.Value) switch
        {
            Thing thing when thing != null && !(thing is ILogicable) => ApiErrors.Refused("device_not_found",
                $"{thing.DisplayName} ({id}) is not a device: it has no logic. container_contents shows the slots of "
                + "any thing."),
            Item item when item != null => ApiErrors.Refused("device_not_found",
                $"{item.DisplayName} ({id}) is an item, not a device: the device tools reach a logic item only "
                + "while a player wears or holds it. container_contents shows the slots of any thing."),
            Thing thing when thing != null && !(thing is Device) => ApiErrors.Refused("device_not_found",
                $"{thing.DisplayName} ({id}) is not a device: it has logic of its own but is not in the game's device "
                + "list, so the device tools do not reach it (list_devices shows what they reach)."),
            _ => ApiErrors.Refused("device_not_found",
                $"Device {id} is not visible {scope.Where} and is not worn or held by a player."),
        };

    /// <summary>The scoped target Require would return, or null when the scope does not reach that id.</summary>
    internal static ScopedTarget? Find(DeviceScope scope, long referenceId)
    {
        ScopedTarget? device = null;
        if (scope.IsWorld)
        {
            if (Thing.Find(referenceId) is Device found && found != null && IsInAllDevices(found))
            {
                device = ScopedTarget.From(found);
            }
        }
        else if (scope.GatewayDevices()!.TryGetValue(referenceId, out Device listed))
        {
            device = ScopedTarget.From(listed);
        }

        if (device == null || device.ReferenceId != referenceId)
        {
            device = ScopedTarget.FindWorn(referenceId);
        }

        return device;
    }

    /// <summary>The circuit holder named by reference_id (see CircuitHolders.Require for the ids accepted).</summary>
    internal static IcTarget RequireCircuitHolder(DeviceScope scope, Args args) =>
        CircuitHolders.Require(scope, args.ThingId("reference_id"));

    // Device.AllDevices membership without walking it: a Device keeps its slot in that pool in the private
    // Device._deviceDensePool (DensePoolReference, Slot -1 when not in the pool; Device.OnAddToPool).
    internal static bool IsInAllDevices(Device device) =>
        GameMembers.DeviceDensePool.GetValue(device) is DensePoolReference<Device> slot && slot.Slot >= 0;

    /// <summary>A device as the device tools list it.</summary>
    internal static DeviceView ViewOf(ScopedTarget device, DeviceScope scope) =>
        new DeviceView(new ThingView(new ThingId(device.ReferenceId), device.PrefabName, device.DisplayName),
            device.PrefabHash, device.Thing.GetType().FullName, SlotCount(device),
            new DeviceWear(scope.IsGatewayItself(device), device.WornBy, device.WornSlot));

    private static int SlotCount(ScopedTarget device)
    {
        try
        {
            return device.TotalSlots;
        }
        catch (Exception)
        {
            // ILogicable.TotalSlots on a device whose slots are not set up yet.
            return 0;
        }
    }

    /// <summary>ILogicable.TotalSlots, or the slot list's length when that throws.</summary>
    internal static int TotalSlots(ScopedTarget device)
    {
        try
        {
            return Math.Max(0, device.TotalSlots);
        }
        catch (Exception)
        {
            // ILogicable.TotalSlots on a device whose slots are not set up yet.
            return device.Slots != null ? device.Slots.Count : 0;
        }
    }
}
