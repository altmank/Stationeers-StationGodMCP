#nullable enable

using System.Collections.Generic;

namespace StationGodMCP.Pure;

/// <summary>
/// A loop of a network: a group of pieces joined in more than one way (a 2-edge-connected part of the piece graph),
/// the pieces cut from it, and why it stays whole where it does.
/// </summary>
internal sealed class NetworkLoop
{
    internal NetworkLoop(int index, List<long> pieces, bool spared)
    {
        Index = index;
        Pieces = pieces;
        Spared = spared;
    }

    internal int Index { get; }

    /// <summary>Every piece on the loop, sorted.</summary>
    internal List<long> Pieces { get; }

    /// <summary>A keep id is on it: nothing is cut.</summary>
    internal bool Spared { get; }

    /// <summary>The pieces removed to break it, sorted.</summary>
    internal List<long> Cut { get; } = new List<long>();

    /// <summary>Why a cycle of it stays after the cuts (every way round it runs through a piece that may not go).</summary>
    internal string? Unbroken { get; set; }
}

/// <summary>The loops found and every piece to cut.</summary>
internal sealed class LoopResult
{
    internal LoopResult(List<NetworkLoop> loops)
    {
        Loops = loops;
    }

    internal List<NetworkLoop> Loops { get; }

    internal List<long> Cut
    {
        get
        {
            List<long> cut = new List<long>();
            foreach (NetworkLoop loop in Loops)
            {
                cut.AddRange(loop.Cut);
            }

            cut.Sort();
            return cut;
        }
    }
}

/// <summary>
/// remove_loops on plain values. Pieces link by the game's rule (Connectivity.Links either way); devices take part
/// only as what a piece must stay linked to, never as a way through (a device's ports are separate networks). A
/// chain is a run of pieces with exactly two links each, none linked to a device, between two anchors (any other
/// piece); it may go only when every piece in it is a candidate and not blocked. Chains are the unit of cutting: removing a whole chain leaves no stub, and its two
/// anchors lose one end each (simplify_junctions then fits them). A loop is a group of chains joined in more than one
/// way (bridges of the chain graph removed); each is broken by removing, one at a time, the chain with the fewest
/// pieces whose anchors stay joined without it, until no cycle is left or none of its chains may go. A chain of no
/// pieces (two junctions joined directly) is never cut: that would change pieces that stay, not remove any. A loop
/// holding a keep id is spared whole.
/// </summary>
internal static class LoopCutting
{
    internal const string NoRemovableChain =
        "every way round it runs through a junction, a device link, or a piece that may not be removed";

    internal static LoopResult Find(IReadOnlyList<PieceModel> candidates, IReadOnlyList<PieceModel> others,
        HashSet<long> devices, IReadOnlyDictionary<long, string> blocked, HashSet<long> keep)
    {
        Graph graph = Graph.Of(candidates, others, devices);
        HashSet<long> candidateIds = new HashSet<long>();
        foreach (PieceModel candidate in candidates)
        {
            candidateIds.Add(candidate.Id);
        }

        List<Chain> chains = Chains(graph, candidateIds, blocked);
        List<NetworkLoop> loops = new List<NetworkLoop>();
        foreach (List<Chain> group in Groups(chains))
        {
            List<long> pieces = PiecesOf(group);
            bool spared = pieces.Exists(keep.Contains);
            NetworkLoop loop = new NetworkLoop(loops.Count, pieces, spared);
            if (!spared)
            {
                Break(loop, group, chains);
            }

            loops.Add(loop);
        }

        return new LoopResult(loops);
    }

    // Removes the cheapest chain that leaves its anchors joined, again and again, until the group has no cycle.
    private static void Break(NetworkLoop loop, List<Chain> group, List<Chain> all)
    {
        HashSet<Chain> present = new HashSet<Chain>(all);
        while (true)
        {
            Chain? best = null;
            foreach (Chain chain in group)
            {
                if (!present.Contains(chain) || !chain.Removable || chain.Interior.Count == 0 ||
                    !StillJoined(chain, present))
                {
                    continue;
                }

                if (best == null || chain.Interior.Count < best.Interior.Count ||
                    (chain.Interior.Count == best.Interior.Count && chain.MinimumId < best.MinimumId))
                {
                    best = chain;
                }
            }

            if (best == null)
            {
                break;
            }

            present.Remove(best);
            loop.Cut.AddRange(best.Interior);
        }

        loop.Cut.Sort();
        if (CycleRank(group, present) > 0)
        {
            loop.Unbroken = NoRemovableChain;
        }
    }

    // Whether the chain's anchors stay joined through the other chains present.
    private static bool StillJoined(Chain removed, HashSet<Chain> present)
    {
        if (removed.From == removed.To)
        {
            return true;
        }

        Dictionary<long, List<long>> next = new Dictionary<long, List<long>>();
        foreach (Chain chain in present)
        {
            if (chain != removed)
            {
                Add(next, chain.From, chain.To);
                Add(next, chain.To, chain.From);
            }
        }

        HashSet<long> seen = new HashSet<long> { removed.From };
        Queue<long> queue = new Queue<long>();
        queue.Enqueue(removed.From);
        while (queue.Count > 0)
        {
            long at = queue.Dequeue();
            if (at == removed.To)
            {
                return true;
            }

            if (next.TryGetValue(at, out List<long>? around))
            {
                foreach (long neighbour in around)
                {
                    if (seen.Add(neighbour))
                    {
                        queue.Enqueue(neighbour);
                    }
                }
            }
        }

        return false;
    }

    // Independent cycles left among the group's chains still present: edges - nodes + parts.
    private static int CycleRank(List<Chain> group, HashSet<Chain> present)
    {
        Dictionary<long, long> parent = new Dictionary<long, long>();
        int edges = 0;
        foreach (Chain chain in group)
        {
            if (!present.Contains(chain))
            {
                continue;
            }

            edges++;
            parent.TryAdd(chain.From, chain.From);
            parent.TryAdd(chain.To, chain.To);
            parent[Root(parent, chain.From)] = Root(parent, chain.To);
        }

        int parts = 0;
        foreach (KeyValuePair<long, long> node in parent)
        {
            parts += Root(parent, node.Key) == node.Key ? 1 : 0;
        }

        return edges - parent.Count + parts;
    }

    private static long Root(Dictionary<long, long> parent, long node)
    {
        while (parent[node] != node)
        {
            parent[node] = parent[parent[node]];
            node = parent[node];
        }

        return node;
    }

    // The chains between anchors. A piece is inside a chain when it has exactly two links and none to a device; every
    // other piece is an anchor. A chain may go only when every piece inside it is a candidate and not blocked, so a
    // cut never leaves a stub behind a piece that stays.
    private static List<Chain> Chains(Graph graph, HashSet<long> candidates, IReadOnlyDictionary<long, string> blocked)
    {
        HashSet<long> inner = new HashSet<long>();
        foreach (KeyValuePair<long, List<long>> node in graph.Links)
        {
            if (!graph.DeviceLinked.Contains(node.Key) && node.Value.Count == 2 && node.Value[0] != node.Value[1])
            {
                inner.Add(node.Key);
            }
        }

        List<Chain> chains = new List<Chain>();
        HashSet<(long, long)> walked = new HashSet<(long, long)>();
        List<long> anchors = new List<long>();
        foreach (long node in graph.Links.Keys)
        {
            if (!inner.Contains(node))
            {
                anchors.Add(node);
            }
        }

        anchors.Sort();
        foreach (long anchor in anchors)
        {
            foreach (long first in graph.Links[anchor])
            {
                if (walked.Contains((anchor, first)))
                {
                    continue;
                }

                List<long> interior = new List<long>();
                long previous = anchor;
                long at = first;
                while (inner.Contains(at))
                {
                    interior.Add(at);
                    List<long> around = graph.Links[at];
                    long next = around[0] == previous ? around[1] : around[0];
                    previous = at;
                    at = next;
                }

                walked.Add((anchor, first));
                walked.Add((at, previous));
                bool removable = interior.TrueForAll(id => candidates.Contains(id) && !blocked.ContainsKey(id));
                chains.Add(new Chain(anchor, at, interior, removable));
            }
        }

        // A ring of two-link pieces has no anchor: any of its pieces anchors it (a chain from it back to itself).
        HashSet<long> walkedInner = new HashSet<long>();
        foreach (Chain chain in chains)
        {
            walkedInner.UnionWith(chain.Interior);
        }

        List<long> rest = new List<long>(inner);
        rest.Sort();
        foreach (long start in rest)
        {
            if (walkedInner.Contains(start))
            {
                continue;
            }

            List<long> interior = new List<long>();
            long previous = start;
            long at = graph.Links[start][0];
            while (at != start)
            {
                interior.Add(at);
                List<long> around = graph.Links[at];
                long next = around[0] == previous ? around[1] : around[0];
                previous = at;
                at = next;
            }

            walkedInner.Add(start);
            walkedInner.UnionWith(interior);
            // One piece breaks a ring: its first removable piece stands for the whole ring as one chain.
            int first = interior.FindIndex(id => candidates.Contains(id) && !blocked.ContainsKey(id));
            chains.Add(first >= 0
                ? new Chain(start, start, new List<long> { interior[first] }, true)
                : new Chain(start, start, interior, false));
        }

        return chains;
    }

    // Groups of chains joined in more than one way: the chain graph without its bridges, parts with a cycle.
    private static List<List<Chain>> Groups(List<Chain> chains)
    {
        HashSet<Chain> all = new HashSet<Chain>(chains);
        List<Chain> onCycles = chains.FindAll(chain => StillJoined(chain, all));
        Dictionary<long, long> parent = new Dictionary<long, long>();
        foreach (Chain chain in onCycles)
        {
            parent.TryAdd(chain.From, chain.From);
            parent.TryAdd(chain.To, chain.To);
            parent[Root(parent, chain.From)] = Root(parent, chain.To);
        }

        Dictionary<long, List<Chain>> byRoot = new Dictionary<long, List<Chain>>();
        foreach (Chain chain in onCycles)
        {
            long root = Root(parent, chain.From);
            if (!byRoot.TryGetValue(root, out List<Chain>? group))
            {
                group = new List<Chain>();
                byRoot[root] = group;
            }

            group.Add(chain);
        }

        List<List<Chain>> groups = new List<List<Chain>>(byRoot.Values);
        groups.Sort((a, b) => PiecesOf(a)[0].CompareTo(PiecesOf(b)[0]));
        return groups;
    }

    private static List<long> PiecesOf(List<Chain> group)
    {
        HashSet<long> pieces = new HashSet<long>();
        foreach (Chain chain in group)
        {
            pieces.Add(chain.From);
            pieces.Add(chain.To);
            pieces.UnionWith(chain.Interior);
        }

        List<long> sorted = new List<long>(pieces);
        sorted.Sort();
        return sorted;
    }

    private static void Add(Dictionary<long, List<long>> next, long a, long b)
    {
        if (!next.TryGetValue(a, out List<long>? list))
        {
            list = new List<long>();
            next[a] = list;
        }

        list.Add(b);
    }

    private sealed class Chain
    {
        internal Chain(long from, long to, List<long> interior, bool removable)
        {
            From = from;
            To = to;
            Interior = interior;
            Removable = removable;
            MinimumId = interior.Count > 0 ? Min(interior) : long.MaxValue;
        }

        internal long From { get; }

        internal long To { get; }

        internal List<long> Interior { get; }

        /// <summary>Every piece inside is a candidate and not blocked.</summary>
        internal bool Removable { get; }

        internal long MinimumId { get; }

        private static long Min(List<long> ids)
        {
            long minimum = ids[0];
            foreach (long id in ids)
            {
                minimum = System.Math.Min(minimum, id);
            }

            return minimum;
        }
    }

    /// <summary>The pieces' links to each other (devices left out) and which pieces link to a device.</summary>
    private sealed class Graph
    {
        private Graph(Dictionary<long, List<long>> links, HashSet<long> deviceLinked)
        {
            Links = links;
            DeviceLinked = deviceLinked;
        }

        internal Dictionary<long, List<long>> Links { get; }

        internal HashSet<long> DeviceLinked { get; }

        internal static Graph Of(IReadOnlyList<PieceModel> candidates, IReadOnlyList<PieceModel> others,
            HashSet<long> devices)
        {
            List<PieceModel> models = new List<PieceModel>(candidates);
            models.AddRange(others);
            Dictionary<GridCell, List<PieceModel>> byCell = new Dictionary<GridCell, List<PieceModel>>();
            Dictionary<long, List<long>> links = new Dictionary<long, List<long>>();
            foreach (PieceModel model in models)
            {
                if (!devices.Contains(model.Id))
                {
                    links[model.Id] = new List<long>();
                }

                foreach (GridCell cell in model.Cells)
                {
                    if (!byCell.TryGetValue(cell, out List<PieceModel>? list))
                    {
                        list = new List<PieceModel>(2);
                        byCell[cell] = list;
                    }

                    list.Add(model);
                }
            }

            HashSet<long> deviceLinked = new HashSet<long>();
            HashSet<(long, long)> seen = new HashSet<(long, long)>();
            foreach (PieceModel model in models)
            {
                foreach (PieceEnd end in model.Ends)
                {
                    if (!byCell.TryGetValue(end.Local, out List<PieceModel>? there))
                    {
                        continue;
                    }

                    foreach (PieceModel other in there)
                    {
                        if (other.Id == model.Id || !Connectivity.Links(model, other))
                        {
                            continue;
                        }

                        Join(model.Id, other.Id, devices, links, deviceLinked, seen);
                    }
                }
            }

            return new Graph(links, deviceLinked);
        }

        private static void Join(long a, long b, HashSet<long> devices, Dictionary<long, List<long>> links,
            HashSet<long> deviceLinked, HashSet<(long, long)> seen)
        {
            if (devices.Contains(a) || devices.Contains(b))
            {
                if (!devices.Contains(a))
                {
                    deviceLinked.Add(a);
                }

                if (!devices.Contains(b))
                {
                    deviceLinked.Add(b);
                }

                return;
            }

            (long, long) key = a < b ? (a, b) : (b, a);
            if (seen.Add(key))
            {
                links[a].Add(b);
                links[b].Add(a);
            }
        }
    }
}
