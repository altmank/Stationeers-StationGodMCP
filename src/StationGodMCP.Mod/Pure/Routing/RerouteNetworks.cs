#nullable enable

using System.Collections.Generic;
using System.Globalization;

namespace StationGodMCP.Pure;

/// <summary>
/// One way a reroute end meets the kind's networks: a device port (Port, its OpenEnds index) or a piece itself (Port
/// null), and the network there (null: nothing of the kind joins that port).
/// </summary>
internal sealed class EndNetwork
{
    internal EndNetwork(int? port, long? network)
    {
        Port = port;
        Network = network;
    }

    internal int? Port { get; }

    internal long? Network { get; }

    internal string PortName => Port.HasValue ? $"port {Port.Value.ToString(CultureInfo.InvariantCulture)}" : "itself";

    public override string ToString()
    {
        string network = Network.HasValue
            ? $"network {Network.Value.ToString(CultureInfo.InvariantCulture)}"
            : "nothing joined";
        return Port.HasValue ? $"{PortName}: {network}" : network;
    }
}

/// <summary>A reroute end as named (for messages: "StructureAreaPowerControl 123") with how it meets the networks.</summary>
internal sealed class RerouteEndNetworks
{
    internal RerouteEndNetworks(string name, IReadOnlyList<EndNetwork> networks)
    {
        Name = name;
        Networks = networks;
    }

    internal string Name { get; }

    internal IReadOnlyList<EndNetwork> Networks { get; }

    internal bool Meets(long network)
    {
        foreach (EndNetwork end in Networks)
        {
            if (end.Network == network)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The ports (or "itself") on the network, as "port 0 or port 2".</summary>
    internal string PortsOn(long network)
    {
        List<string> ports = new List<string>();
        foreach (EndNetwork end in Networks)
        {
            if (end.Network == network)
            {
                ports.Add(end.PortName);
            }
        }

        return string.Join(" or ", ports);
    }

    public override string ToString() => $"{Name} [{string.Join("; ", Networks)}]";
}

/// <summary>The network a reroute between two ends runs through, or why there is not exactly one.</summary>
internal abstract class RerouteNetworkChoice
{
    private RerouteNetworkChoice()
    {
    }

    internal sealed class Chosen : RerouteNetworkChoice
    {
        internal Chosen(long network)
        {
            Network = network;
        }

        internal long Network { get; }
    }

    internal sealed class Refused : RerouteNetworkChoice
    {
        internal Refused(string code, string message)
        {
            Code = code;
            Message = message;
        }

        internal string Code { get; }

        internal string Message { get; }
    }
}

/// <summary>
/// Picks the one network two reroute ends share. A device may sit on several networks of the kind (an APC's input and
/// output, a transformer's two sides, a pump's two pipes), so a device end is the network at each of its ports of the
/// kind (only the named port, when the caller names one); a piece end is its own network. Exactly one shared network
/// is the old run's; none is not_on_one_network, more than one is ambiguous_port, and both messages list every end's
/// ports and networks so the caller can name the port.
/// </summary>
internal static class RerouteNetworks
{
    internal const string NotOnOneNetwork = "not_on_one_network";
    internal const string AmbiguousPort = "ambiguous_port";

    internal static RerouteNetworkChoice Shared(RerouteEndNetworks a, RerouteEndNetworks b, string noun)
    {
        List<long> shared = new List<long>();
        foreach (EndNetwork end in a.Networks)
        {
            if (end.Network is long network && b.Meets(network) && !shared.Contains(network))
            {
                shared.Add(network);
            }
        }

        if (shared.Count == 1)
        {
            return new RerouteNetworkChoice.Chosen(shared[0]);
        }

        if (shared.Count == 0)
        {
            return new RerouteNetworkChoice.Refused(NotOnOneNetwork,
                $"{a.Name} and {b.Name} share no {noun} network: {a}, {b}. The old run must be one network " +
                "joining both ends; connections lists each port's network.");
        }

        List<string> candidates = new List<string>(shared.Count);
        foreach (long network in shared)
        {
            candidates.Add($"network {network.ToString(CultureInfo.InvariantCulture)} " +
                           $"({a.Name} {a.PortsOn(network)}, {b.Name} {b.PortsOn(network)})");
        }

        return new RerouteNetworkChoice.Refused(AmbiguousPort,
            $"{a.Name} and {b.Name} share {shared.Count} {noun} networks: {string.Join("; ", candidates)}. Name " +
            "the port of a device end: between: [{reference_id, port}, ...].");
    }
}
