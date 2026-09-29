#nullable enable

using System.Collections.Generic;

namespace StationGodMCP.Pure;

/// <summary>A network a split leaves: its forecast index, its device ports and whether a root is on it.</summary>
internal sealed class SplitPart
{
    internal SplitPart(int index, List<ForecastPort> ports, bool holdsRoot)
    {
        Index = index;
        Ports = ports;
        HoldsRoot = holdsRoot;
    }

    internal int Index { get; }

    /// <summary>Every device port on this part after the edit.</summary>
    internal List<ForecastPort> Ports { get; }

    internal bool HoldsRoot { get; }
}

/// <summary>
/// A network an edit splits, part by part, with the devices cut off from its root: the devices of every part that
/// holds no root, and every port of the network left joined to nothing. CutOff is null when no root is on the network
/// (nothing to be cut off from; the parts still say which devices end up together).
/// </summary>
internal sealed class SplitDetail
{
    internal SplitDetail(long? network, List<SplitPart> parts, List<long> roots, List<long>? cutOff)
    {
        Network = network;
        Parts = parts;
        Roots = roots;
        CutOff = cutOff;
    }

    /// <summary>The network before the edit (for cut ports, the network every port of the entry was on).</summary>
    internal long? Network { get; }

    internal List<SplitPart> Parts { get; }

    /// <summary>The root devices found on the network, sorted.</summary>
    internal List<long> Roots { get; }

    /// <summary>Devices cut off from every root, sorted; null when the network has no root.</summary>
    internal List<long>? CutOff { get; }
}

/// <summary>
/// What would_split measures against: devices named as the root (every port of theirs counts), and supplier ports, a
/// device with the network it feeds (an APC's or battery's output network, a generator's). A battery's input port is
/// not a root of the network that charges it, though the battery feeds another network.
/// </summary>
internal sealed class NetworkRootSet
{
    private readonly HashSet<long> _named = new HashSet<long>();
    private readonly HashSet<(long Device, long Network)> _feeds = new HashSet<(long Device, long Network)>();

    internal static NetworkRootSet None => new NetworkRootSet();

    internal static NetworkRootSet Named(params long[] devices)
    {
        NetworkRootSet roots = new NetworkRootSet();
        roots._named.UnionWith(devices);
        return roots;
    }

    /// <summary>Whether the caller named the root (a network with none on it is then not the named root's).</summary>
    internal bool IsNamed => _named.Count > 0;

    internal void AddFeed(long device, long network) => _feeds.Add((device, network));

    /// <summary>Whether the port is a root's: its device was named, or it is on a network its device feeds.</summary>
    internal bool Holds(ForecastPort port) => Holds(port.DeviceId, port.NetworkBefore);

    internal bool Holds(long device, long? network) =>
        _named.Contains(device) || (network.HasValue && _feeds.Contains((device, network.Value)));
}

/// <summary>
/// Which devices a split cuts off from the network's root. A root is a device that feeds the network: the caller's
/// root, or every supplier the game side finds (an APC's, transformer's or battery's output, a generator, a solar
/// panel), each only on the network it feeds. A device is cut off when none of its ports on the network is on a part
/// that holds a root after the edit; when a root's own port is cut and the network stays one piece, every device left
/// on it is cut off.
/// </summary>
internal static class SplitAnalysis
{
    internal static List<SplitDetail> Of(Forecast forecast, NetworkRootSet roots)
    {
        List<SplitDetail> details = new List<SplitDetail>(forecast.Splits.Count + 1);
        foreach (ForecastSplit split in forecast.Splits)
        {
            details.Add(OfSplit(forecast, split, roots));
        }

        details.AddRange(OfCutPorts(forecast, roots));
        return details;
    }

    private static SplitDetail OfSplit(Forecast forecast, ForecastSplit split, NetworkRootSet roots)
    {
        List<SplitPart> parts = new List<SplitPart>(split.Parts.Count);
        HashSet<long> present = new HashSet<long>();
        HashSet<long> rooted = new HashSet<long>();
        foreach (int index in split.Parts)
        {
            ForecastNetwork network = forecast.Networks[index];
            bool holdsRoot = false;
            foreach (ForecastPort port in network.Ports)
            {
                if (roots.Holds(port.DeviceId, split.Network))
                {
                    holdsRoot = true;
                    present.Add(port.DeviceId);
                }
            }

            if (holdsRoot)
            {
                foreach (ForecastPort port in network.Ports)
                {
                    rooted.Add(port.DeviceId);
                }
            }

            parts.Add(new SplitPart(index, new List<ForecastPort>(network.Ports), holdsRoot));
        }

        foreach (ForecastPort port in forecast.Cut)
        {
            if (port.NetworkBefore == split.Network && roots.Holds(port))
            {
                present.Add(port.DeviceId);
            }
        }

        List<long> found = Sorted(present);
        if (found.Count == 0)
        {
            return new SplitDetail(split.Network, parts, found, null);
        }

        HashSet<long> cut = new HashSet<long>();
        foreach (SplitPart part in parts)
        {
            if (part.HoldsRoot)
            {
                continue;
            }

            foreach (ForecastPort port in part.Ports)
            {
                if (port.NetworkBefore == split.Network && !rooted.Contains(port.DeviceId) &&
                    !roots.Holds(port.DeviceId, split.Network))
                {
                    cut.Add(port.DeviceId);
                }
            }
        }

        foreach (ForecastPort port in forecast.Cut)
        {
            if (port.NetworkBefore == split.Network && !rooted.Contains(port.DeviceId) &&
                !roots.Holds(port.DeviceId, split.Network))
            {
                cut.Add(port.DeviceId);
            }
        }

        return new SplitDetail(split.Network, parts, found, Sorted(cut));
    }

    // Ports joined to nothing after the edit, one entry per network they were on (in id order): every device among
    // them is cut off, unless it is a root itself or keeps another port on a network. A root port cut from a network
    // that stays whole cuts off every device left on it that no other root feeds. Roots are the network's own: those
    // among its cut ports and those its ports keep after the edit; with none, nothing is called cut off (null).
    private static IEnumerable<SplitDetail> OfCutPorts(Forecast forecast, NetworkRootSet roots)
    {
        HashSet<long> stillJoined = new HashSet<long>();
        foreach (ForecastNetwork network in forecast.Networks)
        {
            foreach (ForecastPort port in network.Ports)
            {
                stillJoined.Add(port.DeviceId);
            }
        }

        SortedDictionary<long, List<ForecastPort>> byNetwork = new SortedDictionary<long, List<ForecastPort>>();
        foreach (ForecastPort port in forecast.Cut)
        {
            long network = port.NetworkBefore ?? 0;
            if (!byNetwork.TryGetValue(network, out List<ForecastPort> ports))
            {
                ports = new List<ForecastPort>();
                byNetwork[network] = ports;
            }

            ports.Add(port);
        }

        foreach (KeyValuePair<long, List<ForecastPort>> group in byNetwork)
        {
            yield return OfCutNetwork(forecast, group.Key, group.Value, stillJoined, roots);
        }
    }

    private static SplitDetail OfCutNetwork(Forecast forecast, long network, List<ForecastPort> ports,
        HashSet<long> stillJoined, NetworkRootSet roots)
    {
        HashSet<long> cut = new HashSet<long>();
        HashSet<long> present = new HashSet<long>();
        bool rootCut = false;
        foreach (ForecastPort port in ports)
        {
            if (roots.Holds(port))
            {
                present.Add(port.DeviceId);
                rootCut = true;
            }
            else if (!stillJoined.Contains(port.DeviceId))
            {
                cut.Add(port.DeviceId);
            }
        }

        foreach (ForecastNetwork after in forecast.Networks)
        {
            foreach (ForecastPort port in after.Ports)
            {
                if (port.NetworkBefore == network && roots.Holds(port.DeviceId, network))
                {
                    present.Add(port.DeviceId);
                }
            }
        }

        if (rootCut && !forecast.Splits.Exists(split => split.Network == network))
        {
            cut.UnionWith(LeftWithoutRoot(forecast, network, roots));
        }

        cut.ExceptWith(present);
        return new SplitDetail(network, new List<SplitPart>(), Sorted(present),
            present.Count > 0 ? Sorted(cut) : null);
    }

    // The devices whose ports stay on what is left of the network when no port left on it is a root's.
    private static List<long> LeftWithoutRoot(Forecast forecast, long network, NetworkRootSet roots)
    {
        List<long> devices = new List<long>();
        foreach (ForecastNetwork after in forecast.Networks)
        {
            if (!after.NetworksBefore.Contains(network) ||
                after.Ports.Exists(port => roots.Holds(port.DeviceId, network)))
            {
                continue;
            }

            foreach (ForecastPort port in after.Ports)
            {
                if (port.NetworkBefore == network)
                {
                    devices.Add(port.DeviceId);
                }
            }
        }

        return devices;
    }

    /// <summary>The cut-port entry for the port's network (Of lists the splits first, then one per network).</summary>
    internal static SplitDetail? CutEntryOf(List<SplitDetail> details, int splits, ForecastPort port)
    {
        for (int index = splits; index < details.Count; index++)
        {
            if (details[index].Network == (port.NetworkBefore ?? 0))
            {
                return details[index];
            }
        }

        return null;
    }

    /// <summary>A one-line summary for a would_split message: each part's devices, and those cut off.</summary>
    internal static string Describe(SplitDetail detail, bool rootNamed = false)
    {
        List<string> parts = new List<string>(detail.Parts.Count);
        foreach (SplitPart part in detail.Parts)
        {
            List<long> devices = new List<long>();
            foreach (ForecastPort port in part.Ports)
            {
                if (!devices.Contains(port.DeviceId))
                {
                    devices.Add(port.DeviceId);
                }
            }

            devices.Sort();
            parts.Add($"[{string.Join(", ", devices)}]{(part.HoldsRoot ? " (root)" : string.Empty)}");
        }

        string layout = parts.Count > 0 ? $" Devices per part: {string.Join("; ", parts)}." : string.Empty;
        string cut = detail.CutOff == null
            ? rootNamed
                ? " The root named is not on it."
                : " No root is on it (pass root to name one)."
            : detail.CutOff.Count == 0
                ? " No device is cut off from the root."
                : $" Cut off from the root: {string.Join(", ", detail.CutOff)}.";
        return layout + cut;
    }

    private static List<long> Sorted(HashSet<long> ids)
    {
        List<long> list = new List<long>(ids);
        list.Sort();
        return list;
    }
}
