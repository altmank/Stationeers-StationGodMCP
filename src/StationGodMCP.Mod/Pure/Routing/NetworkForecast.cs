#nullable enable

using System.Collections.Generic;

namespace StationGodMCP.Pure;

/// <summary>A device port as it is before an edit and what it is joined to after.</summary>
internal sealed class ForecastPort
{
    internal ForecastPort(long deviceId, int index, bool power, long? networkBefore, long? attachedAfter)
    {
        DeviceId = deviceId;
        Index = index;
        Power = power;
        NetworkBefore = networkBefore;
        AttachedAfter = attachedAfter;
    }

    internal long DeviceId { get; }

    internal int Index { get; }

    internal bool Power { get; }

    /// <summary>The network the port is on now; null when nothing is joined to it.</summary>
    internal long? NetworkBefore { get; }

    /// <summary>The node (piece or whole network) joined to it after the edit; null for none.</summary>
    internal long? AttachedAfter { get; }
}

/// <summary>
/// An edit to one family's networks as a graph after the edit. A network the edit takes nothing from stays whole, so
/// it is one node (its own id); a network the edit takes pieces from is modelled piece by piece, each remaining piece
/// a node under that network, so a split shows. New pieces are nodes of no network. Links join nodes; device ports
/// hang on the node they are joined to after the edit, and a device never joins its own ports (an APC's input and
/// output stay two networks unless the cables join them).
/// </summary>
internal sealed class NetworkEdit
{
    internal Dictionary<long, long?> Nodes { get; } = new Dictionary<long, long?>();

    internal HashSet<long> WholeNetworks { get; } = new HashSet<long>();

    internal List<KeyValuePair<long, long>> Links { get; } = new List<KeyValuePair<long, long>>();

    internal List<ForecastPort> Ports { get; } = new List<ForecastPort>();

    /// <summary>Removed pieces by id, with the network each was in.</summary>
    internal Dictionary<long, long> Removed { get; } = new Dictionary<long, long>();

    internal void AddNetwork(long network)
    {
        WholeNetworks.Add(network);
        Nodes[network] = network;
    }

    internal void AddPiece(long piece, long? network) => Nodes[piece] = network;

    internal void Link(long a, long b) => Links.Add(new KeyValuePair<long, long>(a, b));

    internal void Remove(long piece, long network) => Removed[piece] = network;
}

/// <summary>One network after the edit: the networks it holds pieces of, its modelled and new pieces, its ports.</summary>
internal sealed class ForecastNetwork
{
    internal ForecastNetwork(int index)
    {
        Index = index;
    }

    /// <summary>A number for this forecast only; the game gives merged and split networks their ids itself.</summary>
    internal int Index { get; }

    /// <summary>The networks before the edit it holds (all or part of), sorted.</summary>
    internal List<long> NetworksBefore { get; } = new List<long>();

    /// <summary>Its new pieces (nodes of no network).</summary>
    internal List<long> NewPieces { get; } = new List<long>();

    internal List<ForecastPort> Ports { get; } = new List<ForecastPort>();
}

/// <summary>A network before the edit whose remaining pieces fall into more than one network after it.</summary>
internal sealed class ForecastSplit
{
    internal ForecastSplit(long network, List<int> parts)
    {
        Network = network;
        Parts = parts;
    }

    internal long Network { get; }

    /// <summary>ForecastNetwork indexes, sorted.</summary>
    internal List<int> Parts { get; }
}

/// <summary>A device whose power ports end on one network after the edit though they were not before.</summary>
internal sealed class ForecastBridge
{
    internal ForecastBridge(long device, List<ForecastPort> ports, int network)
    {
        Device = device;
        Ports = ports;
        Network = network;
    }

    internal long Device { get; }

    internal List<ForecastPort> Ports { get; }

    internal int Network { get; }
}

/// <summary>The networks an edit leaves, and every merge, split, device bridge and cut port it makes.</summary>
internal sealed class Forecast
{
    internal List<ForecastNetwork> Networks { get; } = new List<ForecastNetwork>();

    /// <summary>Networks after the edit holding pieces of two or more networks before it.</summary>
    internal List<ForecastNetwork> Merges { get; } = new List<ForecastNetwork>();

    internal List<ForecastSplit> Splits { get; } = new List<ForecastSplit>();

    internal List<ForecastBridge> Bridges { get; } = new List<ForecastBridge>();

    /// <summary>Ports joined to a network before the edit and to nothing after it.</summary>
    internal List<ForecastPort> Cut { get; } = new List<ForecastPort>();

    /// <summary>Networks before the edit with every piece removed.</summary>
    internal List<long> Gone { get; } = new List<long>();

    internal ForecastNetwork? NetworkOf(long node, Dictionary<long, int> componentOf) =>
        componentOf.TryGetValue(node, out int index) ? Networks[index] : null;
}

/// <summary>
/// The networks after an edit, on plain values: the connected parts of the edit's graph (union-find over its links),
/// then which parts hold pieces of several networks (a merge), which networks' remaining pieces fall into several
/// parts (a split), which devices get two power ports on one part (a bridge through the device: both sides of an
/// APC or transformer, a battery's input and output), and which ports lose everything they were joined to.
/// </summary>
internal static class NetworkForecaster
{
    internal static Forecast Of(NetworkEdit edit, out Dictionary<long, int> componentOf)
    {
        Dictionary<long, long> parent = new Dictionary<long, long>();
        foreach (long node in edit.Nodes.Keys)
        {
            parent[node] = node;
        }

        foreach (KeyValuePair<long, long> link in edit.Links)
        {
            if (parent.ContainsKey(link.Key) && parent.ContainsKey(link.Value))
            {
                Union(parent, link.Key, link.Value);
            }
        }

        Forecast forecast = new Forecast();
        componentOf = new Dictionary<long, int>();
        Dictionary<long, int> byRoot = new Dictionary<long, int>();
        List<long> ordered = new List<long>(edit.Nodes.Keys);
        ordered.Sort();
        foreach (long node in ordered)
        {
            long root = Find(parent, node);
            if (!byRoot.TryGetValue(root, out int index))
            {
                index = forecast.Networks.Count;
                byRoot[root] = index;
                forecast.Networks.Add(new ForecastNetwork(index));
            }

            componentOf[node] = index;
            ForecastNetwork network = forecast.Networks[index];
            long? before = edit.Nodes[node];
            if (before.HasValue && !network.NetworksBefore.Contains(before.Value))
            {
                network.NetworksBefore.Add(before.Value);
            }
            else if (!before.HasValue)
            {
                network.NewPieces.Add(node);
            }
        }

        foreach (ForecastNetwork network in forecast.Networks)
        {
            network.NetworksBefore.Sort();
            if (network.NetworksBefore.Count > 1)
            {
                forecast.Merges.Add(network);
            }
        }

        Splits(edit, forecast, componentOf);
        Ports(edit, forecast, componentOf);
        return forecast;
    }

    private static void Splits(NetworkEdit edit, Forecast forecast, Dictionary<long, int> componentOf)
    {
        Dictionary<long, List<int>> parts = new Dictionary<long, List<int>>();
        foreach (KeyValuePair<long, long?> node in edit.Nodes)
        {
            if (!node.Value.HasValue || edit.WholeNetworks.Contains(node.Key))
            {
                continue;
            }

            if (!parts.TryGetValue(node.Value.Value, out List<int> list))
            {
                list = new List<int>();
                parts[node.Value.Value] = list;
            }

            int part = componentOf[node.Key];
            if (!list.Contains(part))
            {
                list.Add(part);
            }
        }

        List<long> networks = new List<long>(parts.Keys);
        networks.Sort();
        foreach (long network in networks)
        {
            if (parts[network].Count > 1)
            {
                parts[network].Sort();
                forecast.Splits.Add(new ForecastSplit(network, parts[network]));
            }
        }

        HashSet<long> gone = new HashSet<long>();
        foreach (KeyValuePair<long, long> removed in edit.Removed)
        {
            if (!parts.ContainsKey(removed.Value) && !edit.WholeNetworks.Contains(removed.Value))
            {
                gone.Add(removed.Value);
            }
        }

        forecast.Gone.AddRange(gone);
        forecast.Gone.Sort();
    }

    private static void Ports(NetworkEdit edit, Forecast forecast, Dictionary<long, int> componentOf)
    {
        Dictionary<long, List<ForecastPort>> byDevice = new Dictionary<long, List<ForecastPort>>();
        foreach (ForecastPort port in edit.Ports)
        {
            if (port.AttachedAfter.HasValue && componentOf.TryGetValue(port.AttachedAfter.Value, out int index))
            {
                forecast.Networks[index].Ports.Add(port);
            }
            else if (port.NetworkBefore.HasValue)
            {
                forecast.Cut.Add(port);
            }

            if (!byDevice.TryGetValue(port.DeviceId, out List<ForecastPort> list))
            {
                list = new List<ForecastPort>();
                byDevice[port.DeviceId] = list;
            }

            list.Add(port);
        }

        List<long> devices = new List<long>(byDevice.Keys);
        devices.Sort();
        foreach (long device in devices)
        {
            Bridge(forecast, device, byDevice[device], componentOf);
        }
    }

    // Two power ports on one network after the edit that were not on one network before.
    private static void Bridge(Forecast forecast, long device, List<ForecastPort> ports,
        Dictionary<long, int> componentOf)
    {
        Dictionary<int, List<ForecastPort>> byNetwork = new Dictionary<int, List<ForecastPort>>();
        foreach (ForecastPort port in ports)
        {
            if (!port.Power || !port.AttachedAfter.HasValue ||
                !componentOf.TryGetValue(port.AttachedAfter.Value, out int index))
            {
                continue;
            }

            if (!byNetwork.TryGetValue(index, out List<ForecastPort> list))
            {
                list = new List<ForecastPort>();
                byNetwork[index] = list;
            }

            list.Add(port);
        }

        foreach (KeyValuePair<int, List<ForecastPort>> entry in byNetwork)
        {
            if (entry.Value.Count > 1 && !AllOnOneNetworkBefore(entry.Value))
            {
                forecast.Bridges.Add(new ForecastBridge(device, entry.Value, entry.Key));
            }
        }
    }

    private static bool AllOnOneNetworkBefore(List<ForecastPort> ports)
    {
        long? first = ports[0].NetworkBefore;
        if (!first.HasValue)
        {
            return false;
        }

        foreach (ForecastPort port in ports)
        {
            if (port.NetworkBefore != first)
            {
                return false;
            }
        }

        return true;
    }

    private static long Find(Dictionary<long, long> parent, long node)
    {
        long root = node;
        while (parent[root] != root)
        {
            root = parent[root];
        }

        while (parent[node] != root)
        {
            long next = parent[node];
            parent[node] = root;
            node = next;
        }

        return root;
    }

    private static void Union(Dictionary<long, long> parent, long a, long b)
    {
        long rootA = Find(parent, a);
        long rootB = Find(parent, b);
        if (rootA != rootB)
        {
            parent[rootA < rootB ? rootB : rootA] = rootA < rootB ? rootA : rootB;
        }
    }
}
