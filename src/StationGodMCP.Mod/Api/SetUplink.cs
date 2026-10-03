#nullable enable

using Assets.Scripts;
using Assets.Scripts.Objects.Pipes;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;

namespace StationGodMCP.Api;

/// <summary>
/// set_uplink: point a Logic Rocket Uplink at a downlink, as screwdriver presses on it do (RocketDataUpLink.InteractWith
/// sets ConnectedDataNetTransmitter on the host, which marks it for clients and refreshes the uplink's data network).
/// Only a downlink a press could select is taken (DataLinks.ChoiceById). Writes; host only.
/// </summary>
internal static class SetUplinkApi
{
    internal static UplinkSetView Handle(Args args)
    {
        DeviceScope scope = Devices.Scope(args);
        ScopedTarget target = Devices.Require(scope, args.ThingId("reference_id"));
        ThingId downlinkId = args.ThingId("downlink_id");
        if (!(target.Thing is RocketDataUpLink uplink))
        {
            throw ApiErrors.Refused("not_uplink",
                $"{target.Thing.DisplayName} ({target.Thing.ReferenceId}) is not a Logic Rocket Uplink.");
        }

        if (!GameManager.RunSimulation)
        {
            throw ApiErrors.Refused("not_host", "This game is a multiplayer client; only the host sets uplinks.");
        }

        ITransmitDataNetworkDevices downlink = DataLinks.ChoiceById(uplink, downlinkId.Value) ??
                                               throw ApiErrors.Refused("invalid_downlink",
                                                   $"{downlinkId} is no downlink this uplink can follow: a built, " +
                                                   "logic readable Logic Rocket Downlink. describe_device on the " +
                                                   "uplink lists them in uplink.choices.");
        DownlinkView? previous = DataLinks.DownlinkOf(uplink);
        uplink.ConnectedDataNetTransmitter = downlink;
        return new UplinkSetView(GameLookup.ViewOf(uplink), previous, DataLinks.LinkOf(uplink));
    }
}
