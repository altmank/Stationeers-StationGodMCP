#nullable enable

using System;
using System.Collections.Generic;

namespace StationGodMCP.Pure.Lint;

/// <summary>
/// upstream and downstream: the pipe networks that feed a network, or that it feeds, through the devices that move a
/// flow from their inputs to their outputs (thing.flow: pump, valve, regulator, filter, mixer), followed device by
/// device. A device's port roles give the direction (port.flow in / out); a flow device whose pipe ports carry no
/// direction (a plain valve) passes both ways.
/// </summary>
internal static class LintFlow
{
    internal static void Register(LintLibrary library)
    {
        library
            .Add(new LintFunction("upstream", "(n: network) -> list<network>",
                "The pipe networks that can feed this one through pumps, valves, regulators, filters and mixers " +
                "(their inputs, when one of their outputs is on it), followed on to the networks feeding those; not the network itself.",
                call => Walk(call[0].AsObject, true), cached: true))
            .Add(new LintFunction("downstream", "(n: network) -> list<network>",
                "The pipe networks this one can feed through pumps, valves, regulators, filters and mixers, followed on.",
                call => Walk(call[0].AsObject, false), cached: true));
    }

    private static LintValue Walk(ILintObject start, bool up)
    {
        List<LintValue> found = new List<LintValue>();
        HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal) { start.Key };
        Queue<ILintObject> queue = new Queue<ILintObject>();
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            ILintObject network = queue.Dequeue();
            foreach (ILintObject next in Neighbours(network, up))
            {
                if (seen.Add(next.Key))
                {
                    found.Add(LintValue.Of(next));
                    queue.Enqueue(next);
                }
            }
        }

        return LintValue.Of(found);
    }

    // The networks one flow device away: across each flow device on the network, from the side touching it to the other.
    private static List<ILintObject> Neighbours(ILintObject network, bool up)
    {
        List<ILintObject> next = new List<ILintObject>();
        foreach (LintValue deviceValue in network.Get(LintModel.Network["devices"]).AsList)
        {
            ILintObject device = deviceValue.AsObject;
            if (device.Get(LintModel.Thing["flow"]).IsNull)
            {
                continue;
            }

            List<(ILintObject Network, string? Flow)> ports = PipePorts(device);
            bool directed = false;
            foreach ((ILintObject _, string? flow) in ports)
            {
                directed |= flow != null;
            }

            foreach ((ILintObject here, string? hereFlow) in ports)
            {
                if (here.Key != network.Key || (directed && hereFlow != (up ? "out" : "in")))
                {
                    continue;
                }

                foreach ((ILintObject there, string? thereFlow) in ports)
                {
                    if (there.Key != network.Key && (!directed || thereFlow == (up ? "in" : "out")))
                    {
                        next.Add(there);
                    }
                }
            }
        }

        return next;
    }

    private static List<(ILintObject Network, string? Flow)> PipePorts(ILintObject device)
    {
        List<(ILintObject, string?)> ports = new List<(ILintObject, string?)>();
        foreach (LintValue portValue in device.Get(LintModel.Thing["ports"]).AsList)
        {
            ILintObject port = portValue.AsObject;
            LintValue network = port.Get(LintModel.Port["network"]);
            if (network.IsNull || network.AsObject.Get(LintModel.Network["kind"]).AsString != "pipe")
            {
                continue;
            }

            LintValue flow = port.Get(LintModel.Port["flow"]);
            ports.Add((network.AsObject, flow.IsNull ? null : flow.AsString));
        }

        return ports;
    }
}
