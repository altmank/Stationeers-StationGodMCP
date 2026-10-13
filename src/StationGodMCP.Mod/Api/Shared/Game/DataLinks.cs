#nullable enable

using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Motherboards;
using Assets.Scripts.Objects.Pipes;
using StationGodMCP.Api.Shared.Game.Build;
using StationGodMCP.Api.Views;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// Logic Rocket Uplinks and the downlinks they follow. An uplink reads the data network of one downlink,
/// RocketDataUpLink.ConnectedDataNetTransmitter; a screwdriver press on its first button steps it to the next entry of
/// RocketDataDownLink.AllITransmitDataNetworkDevices (every built downlink) that is a transmitter, not the uplink
/// itself, and logic readable (Logicable.GetNextValidReadable; RocketDataUpLink.InteractWith).
/// </summary>
internal static class DataLinks
{
    /// <summary>The uplink's link; null when the thing is no uplink.</summary>
    internal static UplinkView? UplinkOf(Thing thing) =>
        thing is RocketDataUpLink uplink ? LinkOf(uplink) : null;

    internal static UplinkView LinkOf(RocketDataUpLink uplink)
    {
        List<ILogicable> all = new List<ILogicable>(RocketDataDownLink.AllITransmitDataNetworkDevices);
        List<DownlinkView> choices = new List<DownlinkView>(all.Count);
        foreach (ILogicable candidate in all)
        {
            if (IsChoice(uplink, candidate) && candidate is Thing thing)
            {
                choices.Add(ViewOf(thing));
            }
        }

        return new UplinkView(DownlinkOf(uplink), uplink.DataConnectionActive(), choices);
    }

    /// <summary>The downlink the uplink follows now; null when none.</summary>
    internal static DownlinkView? DownlinkOf(RocketDataUpLink uplink) =>
        uplink.ConnectedDataNetTransmitter is Thing followed ? ViewOf(followed) : null;

    /// <summary>
    /// Points a Logic Rocket Uplink at a downlink, as screwdriver presses on it do (RocketDataUpLink.InteractWith sets
    /// ConnectedDataNetTransmitter on the host, which marks it for clients and refreshes the uplink's data network).
    /// Only a downlink a press could select is taken (ChoiceById); host only. Answers the uplink, the downlink it
    /// followed before, and its link now.
    /// </summary>
    internal static UplinkSetView Point(ScopedTarget target, ThingId downlinkId)
    {
        if (!(target.Thing is RocketDataUpLink uplink))
        {
            throw ApiErrors.Refused("not_uplink",
                $"{target.Thing.DisplayName} ({target.Thing.ReferenceId}) is not a Logic Rocket Uplink.");
        }

        if (!GameManager.RunSimulation)
        {
            throw ApiErrors.Refused("not_host", "This game is a multiplayer client; only the host points uplinks.");
        }

        ITransmitDataNetworkDevices downlink = ChoiceById(uplink, downlinkId.Value) ??
                                               throw ApiErrors.Refused("invalid_downlink",
                                                   $"downlink: {downlinkId} is no downlink this uplink can follow: a " +
                                                   "built, logic readable Logic Rocket Downlink. inspect device on " +
                                                   "the uplink lists them in uplink.choices.");
        DownlinkView? previous = DownlinkOf(uplink);
        uplink.ConnectedDataNetTransmitter = downlink;
        return new UplinkSetView(GameLookup.ViewOf(uplink), previous, LinkOf(uplink));
    }

    /// <summary>The downlink with that id when a screwdriver press could select it for this uplink; null otherwise.</summary>
    internal static ITransmitDataNetworkDevices? ChoiceById(RocketDataUpLink uplink, long referenceId)
    {
        List<ILogicable> all = new List<ILogicable>(RocketDataDownLink.AllITransmitDataNetworkDevices);
        foreach (ILogicable candidate in all)
        {
            if (candidate is ITransmitDataNetworkDevices transmitter && transmitter.ReferenceId == referenceId &&
                IsChoice(uplink, candidate))
            {
                return transmitter;
            }
        }

        return null;
    }

    private static bool IsChoice(RocketDataUpLink uplink, ILogicable candidate) =>
        candidate is ITransmitDataNetworkDevices && !ReferenceEquals(candidate, uplink) &&
        candidate.IsLogicReadable();

    private static DownlinkView ViewOf(Thing downlink) =>
        new DownlinkView(GameLookup.ViewOf(downlink), RocketReadings.PartOf(downlink));
}
