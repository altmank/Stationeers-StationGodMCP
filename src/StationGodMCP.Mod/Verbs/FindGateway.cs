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
/// find gateway (catalogue find.json): the device scopes a gateway option takes, "world" first, then each StationGod
/// Gateway with its status and counts (DeviceScope.Listing); {gateways, count, total, offset, has_more} (ListReply:
/// the list whole, so offset 0, no limit and has_more false). Read only.
/// </summary>
internal static class FindGateway
{
    internal static VerbOutcome Run(FindGatewayOptions options)
    {
        return WorkResult<List<GatewayView>>.Of(DeviceScope.Listing, gateways =>
        {
            JsonSerializer serializer = ApiJson.Serializer;
            return new VerbOutcome.Answered(ListReply.Of("gateways", gateways, Paging.All,
                gateway => gateway));
        });
    }
}
