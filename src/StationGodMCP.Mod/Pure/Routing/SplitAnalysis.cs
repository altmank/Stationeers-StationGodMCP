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

    /// <summary>The network before the edit; null for the ports cut from networks that stay whole.</summary>
    internal long? Network { get; }

    internal List<SplitPart> Parts { get; }

    /// <summary>The root devices found on the network, sorted.</summary>
    internal List<long> Roots { get; }

    /// <summary>Devices cut off from every root, sorted; null when the network has no root.</summary>
    internal List<long>? CutOff { get; }
}

/// <summary>
/// Which devices a split cuts off from the network's root. A root is a device that feeds the network: the caller's
/// root, or every supplier the game side finds (an APC's, transformer's or battery's output, a generator, a solar
/// panel). A device is cut off when none of its ports on the network is on a part that holds a root after the edit.
/// </summary>
internal static class SplitAnalysis
{
    internal static List<SplitDetail> Of(Forecast forecast, ICollection<long> roots)
    {
        List<SplitDetail> details = new List<SplitDetail>(forecast.Splits.Count + 1);
        foreach (ForecastSplit split in forecast.Splits)
        {
            details.Add(OfSplit(forecast, split, roots));
        }

        if (forecast.Cut.Count > 0)
        {
            details.Add(OfCutPorts(forecast, roots));
        }

        return details;
    }

    private static SplitDetail OfSplit(Forecast forecast, ForecastSplit split, ICollection<long> roots)
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
                if (roots.Contains(port.DeviceId))
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
            if (port.NetworkBefore == split.Network && roots.Contains(port.DeviceId))
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
                    !roots.Contains(port.DeviceId))
                {
                    cut.Add(port.DeviceId);
                }
            }
        }

        foreach (ForecastPort port in forecast.Cut)
        {
            if (port.NetworkBefore == split.Network && !rooted.Contains(port.DeviceId) &&
                !roots.Contains(port.DeviceId))
            {
                cut.Add(port.DeviceId);
            }
        }

        return new SplitDetail(split.Network, parts, found, Sorted(cut));
    }

    // Ports joined to nothing after the edit whose network does not split: every device among them is cut off, unless
    // it is a root itself or keeps another port on a network.
    private static SplitDetail OfCutPorts(Forecast forecast, ICollection<long> roots)
    {
        HashSet<long> stillJoined = new HashSet<long>();
        foreach (ForecastNetwork network in forecast.Networks)
        {
            foreach (ForecastPort port in network.Ports)
            {
                stillJoined.Add(port.DeviceId);
            }
        }

        HashSet<long> cut = new HashSet<long>();
        HashSet<long> present = new HashSet<long>();
        foreach (ForecastPort port in forecast.Cut)
        {
            if (roots.Contains(port.DeviceId))
            {
                present.Add(port.DeviceId);
            }
            else if (!stillJoined.Contains(port.DeviceId))
            {
                cut.Add(port.DeviceId);
            }
        }

        return new SplitDetail(null, new List<SplitPart>(), Sorted(present), Sorted(cut));
    }

    /// <summary>A one-line summary for a would_split message: each part's devices, and those cut off.</summary>
    internal static string Describe(SplitDetail detail)
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
            ? " No root is on it (pass root to name one)."
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
