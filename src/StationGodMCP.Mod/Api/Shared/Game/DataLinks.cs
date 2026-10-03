#nullable enable

using System.Collections.Generic;
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
