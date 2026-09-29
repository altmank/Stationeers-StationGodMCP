#nullable enable

using System;
using System.Collections.Generic;

namespace StationGodMCP.Pure;

/// <summary>
/// A device that passes power from its input network on to its output network (an APC, a transformer, a power
/// transmitter): its input takes what its output network requires, at most its cap. passedW: what its input already
/// takes for its output now (its output network's RequiredLoad, or the cap where the switched-on price already is the
/// cap: a power transmitter off). capW: the most it moves (a transformer's Setting, MaxPowerTransmission), null for
/// none (an APC). A station battery is not one: its input takes its own charge, not its output's demand.
/// </summary>
internal sealed class PassThrough
{
    internal PassThrough(long deviceId, long? outputNetwork, double passedW, double? capW)
    {
        DeviceId = deviceId;
        OutputNetwork = outputNetwork;
        PassedW = passedW;
        CapW = capW;
    }

    internal long DeviceId { get; }

    internal long? OutputNetwork { get; }

    internal double PassedW { get; }

    internal double? CapW { get; }

    /// <summary>What the input takes more when the output network requires behindW more: bounded by the cap.</summary>
    internal double Extra(double behindW) =>
        CapW is double cap
            ? Math.Max(0.0, Math.Min(cap, PassedW + behindW) - Math.Min(cap, PassedW))
            : Math.Max(0.0, behindW);
}

/// <summary>A device off now and what it requires on a network once switched on (PortLoads.Dormant).</summary>
internal sealed class SwitchedOnDemand
{
    internal SwitchedOnDemand(long deviceId, double requiredW)
    {
        DeviceId = deviceId;
        RequiredW = requiredW;
    }

    internal long DeviceId { get; }

    internal double RequiredW { get; }
}

/// <summary>A network behind a pass-through device: its devices off now and the pass-through devices it feeds.</summary>
internal sealed class NetworkBehind
{
    internal NetworkBehind(IReadOnlyList<SwitchedOnDemand> off, IReadOnlyList<PassThrough> feeds)
    {
        Off = off;
        Feeds = feeds;
    }

    internal IReadOnlyList<SwitchedOnDemand> Off { get; }

    internal IReadOnlyList<PassThrough> Feeds { get; }
}

/// <summary>What a pass-through device's input takes more once the devices off behind it are switched on.</summary>
internal sealed class LoadBehind
{
    internal LoadBehind(double requiredW, IReadOnlyList<long> offDevices)
    {
        RequiredW = requiredW;
        OffDevices = offDevices;
    }

    internal static LoadBehind None { get; } = new LoadBehind(0.0, Array.Empty<long>());

    internal double RequiredW { get; }

    /// <summary>The devices off behind it that bring that load, sorted.</summary>
    internal IReadOnlyList<long> OffDevices { get; }
}

/// <summary>
/// The switched-on demand behind a pass-through device, for would_overload_when_on: switching on a device off on an
/// APC's output network raises that network's RequiredLoad, and the APC's input takes it from the network the edit
/// changes. The walk follows each output network's off devices and the pass-through devices whose input sits on it,
/// each bounded by its own cap, at most MaximumDepth devices deep and never into a network already seen (the networks
/// of the edit, one reached another way, or a loop back), so a network is counted once.
/// </summary>
internal static class PassThroughLoads
{
    internal const int MaximumDepth = 8;

    /// <summary>seen: networks not to walk into, grown by the walk (share it across one forecast's ports).</summary>
    internal static LoadBehind Of(PassThrough device, Func<long, NetworkBehind?> networkOf, ISet<long> seen)
    {
        SortedSet<long> off = new SortedSet<long>();
        double required = Walk(device, networkOf, seen, off, 1);
        return required > 0.0 ? new LoadBehind(required, new List<long>(off)) : LoadBehind.None;
    }

    private static double Walk(PassThrough device, Func<long, NetworkBehind?> networkOf, ISet<long> seen,
        SortedSet<long> off, int depth)
    {
        if (depth > MaximumDepth || device.OutputNetwork is not long id || !seen.Add(id) ||
            networkOf(id) is not NetworkBehind network)
        {
            return 0.0;
        }

        double behind = 0.0;
        SortedSet<long> named = new SortedSet<long>();
        foreach (SwitchedOnDemand demand in network.Off)
        {
            if (demand.RequiredW > 0.0)
            {
                behind += demand.RequiredW;
                named.Add(demand.DeviceId);
            }
        }

        foreach (PassThrough feed in network.Feeds)
        {
            if (feed.DeviceId == device.DeviceId)
            {
                continue;
            }

            SortedSet<long> further = new SortedSet<long>();
            double more = Walk(feed, networkOf, seen, further, depth + 1);
            if (more > 0.0)
            {
                behind += more;
                named.UnionWith(further);
            }
        }

        double extra = device.Extra(behind);
        if (extra > 0.0)
        {
            off.UnionWith(named);
        }

        return extra;
    }
}
