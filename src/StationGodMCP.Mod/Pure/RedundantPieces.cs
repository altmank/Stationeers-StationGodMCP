#nullable enable

using System.Collections.Generic;

namespace StationGodMCP.Pure;

/// <summary>Why remove_redundant leaves a candidate piece: its code, and the devices that need it.</summary>
internal sealed class KeptPiece
{
    internal KeptPiece(long id, string reason, List<long> devices, int pieces)
    {
        Id = id;
        Reason = reason;
        Devices = devices;
        Pieces = pieces;
    }

    internal long Id { get; }

    /// <summary>
    /// device_port (it joins a device's port), keep_ids, blocked:&lt;why&gt; (a fuse mounted, no kit...), or needed
    /// (removing it would cut Devices off from the root, or split the network).
    /// </summary>
    internal string Reason { get; }

    /// <summary>For device_port the devices it joins; for needed the devices it would cut off, sorted.</summary>
    internal List<long> Devices { get; }

    /// <summary>For needed: how many kept pieces would be cut off with them.</summary>
    internal int Pieces { get; }

    /// <summary>Why remove_redundant leaves it, as the clean tools' kept_pieces message says it.</summary>
    internal string Message
    {
        get
        {
            string devices = string.Join(", ", Devices);
            if (Reason == RedundantPieces.KeepIds)
            {
                return "remove_redundant: it is in keep_ids, so it never goes.";
            }

            if (Reason == RedundantPieces.DevicePort)
            {
                return $"remove_redundant: it joins a port of {devices}.";
            }

            if (Reason.StartsWith(RedundantPieces.BlockedPrefix, System.StringComparison.Ordinal))
            {
                string why = Reason.Substring(RedundantPieces.BlockedPrefix.Length);
                return $"remove_redundant: it cannot be removed ({why}).";
            }

            return Devices.Count > 0
                ? $"remove_redundant: removing it would cut {devices} off the root."
                : $"remove_redundant: removing it would split the network ({Pieces} piece(s) cut off).";
        }
    }
}

internal sealed class RedundancyResult
{
    internal RedundancyResult(List<long> removed, List<KeptPiece> kept)
    {
        Removed = removed;
        Kept = kept;
    }

    /// <summary>The candidates removed, in the order they were decided.</summary>
    internal List<long> Removed { get; }

    /// <summary>Every candidate left, with why, in candidate order.</summary>
    internal List<KeptPiece> Kept { get; }
}

/// <summary>
/// remove_redundant on plain values: every candidate piece of a network whose removal keeps every remaining piece
/// joined to the rest (so every device stays on one network with its root) is removed, oldest candidate first. A
/// piece joined to a device port, a keep_ids piece and a blocked one never go. Devices never carry a network (a
/// device's two ports on one network do not join it), so the graph is the pieces and their links only; a device
/// hangs on the pieces joined to its ports. Loops that pass through a port piece (an old and a new cable meeting at a
/// device) are therefore ordinary loops here. Greedy with a work list: a candidate refused because it holds the
/// network together is tried again when a neighbour goes (it may have become the end of a dead branch).
/// </summary>
internal static class RedundantPieces
{
    internal const string DevicePort = "device_port";
    internal const string KeepIds = "keep_ids";
    internal const string Needed = "needed";
    internal const string BlockedPrefix = "blocked:";

    /// <param name="pieces">Every piece of the network(s), candidates or not.</param>
    /// <param name="links">Links among pieces and between pieces and devices, either direction.</param>
    /// <param name="devices">The device ids among the links' ends.</param>
    /// <param name="candidates">Pieces that may go, oldest first.</param>
    /// <param name="keep">keep_ids: never removed.</param>
    /// <param name="blocked">Pieces that cannot be removed, with why.</param>
    /// <param name="roots">Devices that feed the network; the part holding one counts as the network's own.</param>
    internal static RedundancyResult Find(IReadOnlyCollection<long> pieces, IEnumerable<Link> links,
        ICollection<long> devices, IReadOnlyList<long> candidates, ICollection<long> keep,
        IReadOnlyDictionary<long, string> blocked, ICollection<long> roots)
    {
        Graph graph = Graph.Of(pieces, links, devices);
        HashSet<long> remaining = new HashSet<long>(pieces);
        Dictionary<long, string> fixedReason = new Dictionary<long, string>();
        foreach (long id in candidates)
        {
            if (graph.PortsOf(id).Count > 0)
            {
                fixedReason[id] = DevicePort;
            }
            else if (keep.Contains(id))
            {
                fixedReason[id] = KeepIds;
            }
            else if (blocked.TryGetValue(id, out string why))
            {
                fixedReason[id] = BlockedPrefix + why;
            }
        }

        Dictionary<long, int> order = new Dictionary<long, int>();
        for (int index = 0; index < candidates.Count; index++)
        {
            order[candidates[index]] = index;
        }

        List<long> removed = new List<long>();
        SortedSet<int> work = new SortedSet<int>();
        for (int index = 0; index < candidates.Count; index++)
        {
            if (!fixedReason.ContainsKey(candidates[index]) && remaining.Contains(candidates[index]))
            {
                work.Add(index);
            }
        }

        while (work.Count > 0)
        {
            int next = work.Min;
            work.Remove(next);
            long id = candidates[next];
            if (!remaining.Contains(id) || !graph.KeepsJoined(id, remaining))
            {
                continue;
            }

            remaining.Remove(id);
            removed.Add(id);
            foreach (long neighbour in graph.Neighbours(id))
            {
                if (remaining.Contains(neighbour) && order.TryGetValue(neighbour, out int at) &&
                    !fixedReason.ContainsKey(neighbour))
                {
                    work.Add(at);
                }
            }
        }

        List<KeptPiece> kept = new List<KeptPiece>();
        foreach (long id in candidates)
        {
            if (!remaining.Contains(id))
            {
                continue;
            }

            if (fixedReason.TryGetValue(id, out string reason))
            {
                kept.Add(new KeptPiece(id, reason, reason == DevicePort ? graph.PortsOf(id) : new List<long>(), 0));
                continue;
            }

            graph.CutOffWithout(id, remaining, roots, out List<long> cutDevices, out int cutPieces);
            kept.Add(new KeptPiece(id, Needed, cutDevices, cutPieces));
        }

        return new RedundancyResult(removed, kept);
    }

    private sealed class Graph
    {
        private readonly Dictionary<long, List<long>> _next = new Dictionary<long, List<long>>();
        private readonly Dictionary<long, List<long>> _ports = new Dictionary<long, List<long>>();

        internal static Graph Of(IReadOnlyCollection<long> pieces, IEnumerable<Link> links, ICollection<long> devices)
        {
            Graph graph = new Graph();
            foreach (long piece in pieces)
            {
                graph._next[piece] = new List<long>(4);
            }

            foreach (Link link in links)
            {
                bool fromDevice = devices.Contains(link.From);
                bool toDevice = devices.Contains(link.To);
                if (fromDevice && graph._next.ContainsKey(link.To))
                {
                    graph.AddPort(link.To, link.From);
                }
                else if (toDevice && graph._next.ContainsKey(link.From))
                {
                    graph.AddPort(link.From, link.To);
                }
                else if (!fromDevice && !toDevice && graph._next.ContainsKey(link.From) &&
                         graph._next.ContainsKey(link.To) && link.From != link.To)
                {
                    AddOnce(graph._next[link.From], link.To);
                    AddOnce(graph._next[link.To], link.From);
                }
            }

            return graph;
        }

        private void AddPort(long piece, long device)
        {
            if (!_ports.TryGetValue(piece, out List<long> list))
            {
                list = new List<long>(1);
                _ports[piece] = list;
            }

            AddOnce(list, device);
            list.Sort();
        }

        private static void AddOnce(List<long> list, long id)
        {
            if (!list.Contains(id))
            {
                list.Add(id);
            }
        }

        internal List<long> PortsOf(long piece) =>
            _ports.TryGetValue(piece, out List<long> list) ? list : new List<long>();

        internal List<long> Neighbours(long piece) =>
            _next.TryGetValue(piece, out List<long> list) ? list : new List<long>();

        /// <summary>Whether every remaining neighbour of the piece still reaches the others without it.</summary>
        internal bool KeepsJoined(long piece, HashSet<long> remaining)
        {
            List<long> around = new List<long>(4);
            foreach (long neighbour in Neighbours(piece))
            {
                if (remaining.Contains(neighbour))
                {
                    around.Add(neighbour);
                }
            }

            if (around.Count <= 1)
            {
                return true;
            }

            HashSet<long> wanted = new HashSet<long>(around);
            HashSet<long> seen = new HashSet<long> { piece, around[0] };
            Queue<long> queue = new Queue<long>();
            queue.Enqueue(around[0]);
            wanted.Remove(around[0]);
            while (queue.Count > 0 && wanted.Count > 0)
            {
                foreach (long other in Neighbours(queue.Dequeue()))
                {
                    if (remaining.Contains(other) && seen.Add(other))
                    {
                        wanted.Remove(other);
                        queue.Enqueue(other);
                    }
                }
            }

            return wanted.Count == 0;
        }

        /// <summary>
        /// The devices and pieces that would lose the network's own part if the piece went: the parts left without it
        /// that hold no root (with no root known, every part but the one with the most devices, then pieces).
        /// </summary>
        internal void CutOffWithout(long piece, HashSet<long> remaining, ICollection<long> roots,
            out List<long> devices, out int pieces)
        {
            List<HashSet<long>> parts = new List<HashSet<long>>();
            HashSet<long> seen = new HashSet<long> { piece };
            foreach (long start in Neighbours(piece))
            {
                if (!remaining.Contains(start) || !seen.Add(start))
                {
                    continue;
                }

                HashSet<long> part = new HashSet<long> { start };
                Queue<long> queue = new Queue<long>();
                queue.Enqueue(start);
                while (queue.Count > 0)
                {
                    foreach (long other in Neighbours(queue.Dequeue()))
                    {
                        if (remaining.Contains(other) && seen.Add(other))
                        {
                            part.Add(other);
                            queue.Enqueue(other);
                        }
                    }
                }

                parts.Add(part);
            }

            int own = OwnPart(parts, roots);
            HashSet<long> cut = new HashSet<long>();
            HashSet<long> ownDevices = new HashSet<long>();
            if (own >= 0)
            {
                foreach (long member in parts[own])
                {
                    ownDevices.UnionWith(PortsOf(member));
                }
            }

            pieces = 0;
            for (int index = 0; index < parts.Count; index++)
            {
                if (index == own)
                {
                    continue;
                }

                pieces += parts[index].Count;
                foreach (long member in parts[index])
                {
                    foreach (long device in PortsOf(member))
                    {
                        if (!ownDevices.Contains(device))
                        {
                            cut.Add(device);
                        }
                    }
                }
            }

            devices = new List<long>(cut);
            devices.Sort();
        }

        private int OwnPart(List<HashSet<long>> parts, ICollection<long> roots)
        {
            int best = -1;
            int bestDevices = -1;
            int bestPieces = -1;
            for (int index = 0; index < parts.Count; index++)
            {
                HashSet<long> devices = new HashSet<long>();
                bool rooted = false;
                foreach (long member in parts[index])
                {
                    foreach (long device in PortsOf(member))
                    {
                        devices.Add(device);
                        rooted |= roots.Contains(device);
                    }
                }

                if (rooted)
                {
                    return index;
                }

                if (devices.Count > bestDevices || (devices.Count == bestDevices && parts[index].Count > bestPieces))
                {
                    best = index;
                    bestDevices = devices.Count;
                    bestPieces = parts[index].Count;
                }
            }

            return best;
        }
    }
}
