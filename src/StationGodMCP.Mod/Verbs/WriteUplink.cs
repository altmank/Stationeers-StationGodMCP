#nullable enable

using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>
/// write uplink (catalogue write.json): a Logic Rocket Uplink pointed at a downlink, as screwdriver presses on it do
/// (DataLinks.Point), the uplink in the gateway's scope. One id. Answers {uplink, previous_downlink, link}. Host only;
/// cannot be undone.
/// </summary>
internal static class WriteUplink
{
    internal static VerbOutcome Run(WriteUplinkOptions options)
    {
        string label = WriteUplinkOptions.Spec.Label;
        DeviceScope scope = Devices.Scope(options.Gateway);
        ScopedTarget uplink = Devices.Require(scope, TargetIds.Of(options.Selector.Ids!.Ids[0], label));
        ThingId downlink = ThingId.TryRead(new Newtonsoft.Json.Linq.JValue(options.Downlink), out ThingId id)
            ? id
            : throw ApiErrors.Refused("invalid_downlink", $"downlink: '{options.Downlink}' is not a thing's id.");
        return new VerbOutcome.Answered(IdAnswers.Payload(DataLinks.Point(uplink, downlink), ApiJson.Fresh()));
    }
}
