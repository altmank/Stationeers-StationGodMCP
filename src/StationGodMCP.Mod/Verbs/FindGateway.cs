#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>
/// find gateway (catalogue find.json): the device scopes a gateway option takes, "world" first, then each StationGod
/// Gateway with its status and counts (DeviceScope.Listed); {gateways, count}. Read only.
/// </summary>
internal static class FindGateway
{
    internal static VerbOutcome Run(FindGatewayOptions options)
    {
        List<GatewayView> gateways = DeviceScope.Listed();
        return new VerbOutcome.Answered(ReplyFields.Of(("gateways", gateways), ("count", gateways.Count)));
    }
}
