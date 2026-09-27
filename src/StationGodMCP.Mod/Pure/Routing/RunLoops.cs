#nullable enable

using System.Collections.Generic;

namespace StationGodMCP.Pure;

/// <summary>
/// Whether an edit closes a loop: a link it makes joins two things already joined another way. The forecast graph's
/// nodes stand for whole networks or single pieces (NetworkEdit), so the links are counted by the pieces they join
/// (each unordered pair once) and then put on nodes: the links the game has now first, whose cycles are the
/// network's own, then every link of a new or changed piece the game does not have now, each one that finds its two
/// nodes already joined closing a new loop (a run whose two ends both reach one network, or a junction joining a
/// network twice).
/// </summary>
internal static class RunLoops
{
    internal static List<Link> Closing(IEnumerable<Link> links, IReadOnlyDictionary<long, long> nodeOf,
        ICollection<Link> before, ICollection<long> edited)
    {
        List<Link> pairs = new List<Link>();
        HashSet<(long, long)> seen = new HashSet<(long, long)>();
        foreach (Link link in links)
        {
            (long, long) key = link.From < link.To ? (link.From, link.To) : (link.To, link.From);
            if (link.From != link.To && nodeOf.ContainsKey(link.From) && nodeOf.ContainsKey(link.To) && seen.Add(key))
            {
                pairs.Add(new Link(key.Item1, key.Item2));
            }
        }

        pairs.Sort((a, b) => a.From != b.From ? a.From.CompareTo(b.From) : a.To.CompareTo(b.To));
        Dictionary<long, long> parent = new Dictionary<long, long>();
        List<Link> closing = new List<Link>();
        foreach (bool touchesEdit in new[] { false, true })
        {
            foreach (Link pair in pairs)
            {
                bool old = (!edited.Contains(pair.From) && !edited.Contains(pair.To)) || before.Contains(pair) ||
                           before.Contains(new Link(pair.To, pair.From));
                if (old == touchesEdit)
                {
                    continue;
                }

                long a = Root(parent, nodeOf[pair.From]);
                long b = Root(parent, nodeOf[pair.To]);
                if (a == b)
                {
                    if (touchesEdit)
                    {
                        closing.Add(pair);
                    }

                    continue;
                }

                parent[a] = b;
            }
        }

        return closing;
    }

    private static long Root(Dictionary<long, long> parent, long node)
    {
        if (!parent.ContainsKey(node))
        {
            parent[node] = node;
            return node;
        }

        while (parent[node] != node)
        {
            parent[node] = parent[parent[node]];
            node = parent[node];
        }

        return node;
    }
}
