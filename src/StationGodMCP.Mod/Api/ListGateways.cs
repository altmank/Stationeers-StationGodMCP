#nullable enable

using System.Collections.Generic;
using Assets.Scripts.Networks;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;

namespace StationGodMCP.Api;

/// <summary>
/// list_gateways: the scopes gateway_id accepts. "world" comes first: every device in the world (Device.AllDevices);
/// its status stays "bypass" for older clients. Then each StationGod Gateway (GatewayRegistry), whose id only narrows
/// a call to its data networks (StationGodGateway.GetDataNetworks). Read only.
/// </summary>
internal static class ListGatewaysApi
{
    internal static GatewaysView Handle(Args args)
    {
        int worn = ScopedTarget.Worn().Count;
        List<GatewayView> gateways = new List<GatewayView>
        {
            new GatewayView(DeviceScope.WorldId, "Whole world", null,
                new GatewayState(true, "bypass"), new GatewayCounts(0, DeviceScope.CountWorld(), worn))
        };

        foreach (StationGodGateway gateway in GatewayRegistry.Snapshot())
        {
            List<CableNetwork> networks = gateway.GetDataNetworks();
            string? unavailable = gateway.GetUnavailableReason();
            gateways.Add(new GatewayView(new ThingId(gateway.ReferenceId).ToString(), gateway.DisplayName,
                gateway.PrefabName,
                new GatewayState(unavailable == null, unavailable ?? "ready"),
                new GatewayCounts(networks.Count, DeviceScope.CountDevices(networks), worn)));
        }

        return new GatewaysView(gateways);
    }
}
