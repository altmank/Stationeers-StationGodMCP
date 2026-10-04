#nullable enable

using System.Collections.Generic;
using System.Globalization;

namespace StationGodMCP.Pure.Routing;

/// <summary>
/// remove_structure's would_split warnings for ports, one line per device instead of one per port: the ports that lose
/// their connection and the network each was on, in the order the forecast lists them.
/// </summary>
internal static class PortSplitSummary
{
    /// <summary>Each device once, with its cut ports as "index (network n)".</summary>
    internal static List<KeyValuePair<long, List<string>>> Of(IEnumerable<ForecastPort> cut)
    {
        List<KeyValuePair<long, List<string>>> devices = new List<KeyValuePair<long, List<string>>>();
        Dictionary<long, List<string>> byDevice = new Dictionary<long, List<string>>();
        foreach (ForecastPort port in cut)
        {
            if (!byDevice.TryGetValue(port.DeviceId, out List<string> ports))
            {
                ports = new List<string>();
                byDevice.Add(port.DeviceId, ports);
                devices.Add(new KeyValuePair<long, List<string>>(port.DeviceId, ports));
            }

            ports.Add(port.NetworkBefore.HasValue
                ? string.Format(CultureInfo.InvariantCulture, "{0} (network {1})", port.Index, port.NetworkBefore.Value)
                : port.Index.ToString(CultureInfo.InvariantCulture));
        }

        return devices;
    }

    /// <summary>The warning's text for one device.</summary>
    internal static string Message(string tool, string device, IReadOnlyList<string> ports) =>
        $"{tool}'s check: {device} would lose its connection on port{(ports.Count == 1 ? "" : "s")} " +
        $"{string.Join(", ", ports)}; remove_structure removes it anyway (verbose: each port in full).";
}
