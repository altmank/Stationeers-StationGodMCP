#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Shared;

namespace StationGodMCP.Api.Views;

/// <summary>A Logic Rocket Downlink an uplink can follow, and the rocket it is part of.</summary>
internal sealed class DownlinkView
{
    internal DownlinkView(ThingView downlink, RocketPartView? rocket)
    {
        ReferenceId = downlink.ReferenceId;
        PrefabName = downlink.PrefabName;
        DisplayName = downlink.DisplayName;
        Rocket = rocket;
    }

    public ThingId ReferenceId { get; }

    public string? PrefabName { get; }

    public string? DisplayName { get; }

    /// <summary>The rocket the downlink is part of; null when it stands on no rocket.</summary>
    public RocketPartView? Rocket { get; }
}

/// <summary>
/// A Logic Rocket Uplink's link: the downlink it follows (RocketDataUpLink.ConnectedDataNetTransmitter) and its
/// rocket, whether the data connection is live (DataConnectionActive: on, powered, operable and the downlink on a data
/// network), and the downlinks a screwdriver press on it would step through.
/// </summary>
internal sealed class UplinkView
{
    internal UplinkView(DownlinkView? downlink, bool connected, List<DownlinkView> choices)
    {
        Downlink = downlink;
        Connected = connected;
        Choices = choices;
    }

    public DownlinkView? Downlink { get; }

    public bool Connected { get; }

    /// <summary>Every downlink it may follow: built, logic readable (Logicable.GetNextValidReadable's rule).</summary>
    public List<DownlinkView> Choices { get; }
}

/// <summary>set_uplink: the uplink, the downlink it followed before, and its link now.</summary>
internal sealed class UplinkSetView
{
    internal UplinkSetView(ThingView uplink, DownlinkView? previous, UplinkView link)
    {
        Uplink = uplink;
        PreviousDownlink = previous;
        Link = link;
    }

    public ThingView Uplink { get; }

    /// <summary>The downlink before the call: pass its reference_id to set it back; null when it followed none.</summary>
    public DownlinkView? PreviousDownlink { get; }

    public UplinkView Link { get; }
}
