#nullable enable

using System.Collections.Generic;

namespace StationGodMCP.Pure;

/// <summary>A thing on a network as the feed report sees it: a piece or a device, and the room it is in (null: none).</summary>
internal sealed class FeedNode
{
    internal FeedNode(long id, bool device, long? room)
    {
        Id = id;
        Device = device;
        Room = room;
    }

    internal long Id { get; }

    internal bool Device { get; }

    internal long? Room { get; }
}

/// <summary>
/// How one device is fed from the root: the pieces between them (root side first), the rooms those pieces pass through
/// in order (consecutive repeats and pieces in no room dropped), the rooms other than the root's and the device's own
/// (fed through other rooms: a daisy chain), and the piece where the feed enters the device's room.
/// </summary>
internal sealed class FeedPath
{
    internal FeedPath(long device, long? room, List<long> pieces, List<long> rooms, List<long> through, long? entry)
    {
        Device = device;
        Room = room;
        Pieces = pieces;
        Rooms = rooms;
        Through = through;
        Entry = entry;
    }

    internal long Device { get; }

    internal long? Room { get; }

    internal List<long> Pieces { get; }

    internal List<long> Rooms { get; }

    internal List<long> Through { get; }

    /// <summary>The first piece of the feed's last stretch in the device's room (pieces in no room may lie between).</summary>
    internal long? Entry { get; }
}

/// <summary>A room's devices on the network and the distinct pieces where their feeds enter it (one is a single feed).</summary>
internal sealed class RoomFeed
{
    internal RoomFeed(long room, List<long> devices, List<long> entries)
    {
        Room = room;
        Devices = devices;
        Entries = entries;
    }

    internal long Room { get; }

    internal List<long> Devices { get; }

    internal List<long> Entries { get; }
}

internal sealed class FeedReport
{
    internal FeedReport(List<FeedPath> paths, List<RoomFeed> rooms, List<long> unreached)
    {
        Paths = paths;
        Rooms = rooms;
        Unreached = unreached;
    }

    /// <summary>Each device reached, nearest first.</summary>
    internal List<FeedPath> Paths { get; }

    internal List<RoomFeed> Rooms { get; }

    /// <summary>Devices on the network no path from the root reaches through pieces.</summary>
    internal List<long> Unreached { get; }
}

/// <summary>
/// feed_paths: the tree a network forms from a root device (an APC's output, a generator), found breadth first over
/// the links (the shortest path to each device; a device is an end, never passed through), and for each device the
/// rooms its feed crosses. A room entered at more than one piece has several feeds.
/// </summary>
internal static class FeedPaths
{
    internal static FeedReport Of(long root, IReadOnlyDictionary<long, FeedNode> nodes, IEnumerable<Link> links)
    {
        Dictionary<long, List<long>> next = Adjacency(nodes, links);
        Dictionary<long, long> parent = new Dictionary<long, long>();
        List<long> order = new List<long>();
        Queue<long> queue = new Queue<long>();
        HashSet<long> seen = new HashSet<long> { root };
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            long id = queue.Dequeue();
            order.Add(id);
            if (id != root && nodes[id].Device)
            {
                continue;
            }

            foreach (long other in next.TryGetValue(id, out List<long> list) ? list : new List<long>())
            {
                if (seen.Add(other))
                {
                    parent[other] = id;
                    queue.Enqueue(other);
                }
            }
        }

        long? rootRoom = nodes.TryGetValue(root, out FeedNode rootNode) ? rootNode.Room : null;
        List<FeedPath> paths = new List<FeedPath>();
        foreach (long id in order)
        {
            if (id != root && nodes[id].Device)
            {
                paths.Add(PathTo(id, root, rootRoom, nodes, parent));
            }
        }

        List<long> unreached = new List<long>();
        foreach (FeedNode node in nodes.Values)
        {
            if (node.Device && node.Id != root && !seen.Contains(node.Id))
            {
                unreached.Add(node.Id);
            }
        }

        unreached.Sort();
        return new FeedReport(paths, RoomsOf(paths), unreached);
    }

    private static Dictionary<long, List<long>> Adjacency(IReadOnlyDictionary<long, FeedNode> nodes,
        IEnumerable<Link> links)
    {
        Dictionary<long, List<long>> next = new Dictionary<long, List<long>>();
        foreach (Link link in links)
        {
            if (link.From == link.To || !nodes.ContainsKey(link.From) || !nodes.ContainsKey(link.To))
            {
                continue;
            }

            Join(next, link.From, link.To);
            Join(next, link.To, link.From);
        }

        foreach (List<long> list in next.Values)
        {
            list.Sort();
        }

        return next;
    }

    private static void Join(Dictionary<long, List<long>> next, long from, long to)
    {
        if (!next.TryGetValue(from, out List<long> list))
        {
            list = new List<long>();
            next[from] = list;
        }

        if (!list.Contains(to))
        {
            list.Add(to);
        }
    }

    private static FeedPath PathTo(long device, long root, long? rootRoom, IReadOnlyDictionary<long, FeedNode> nodes,
        Dictionary<long, long> parent)
    {
        List<long> pieces = new List<long>();
        for (long at = parent[device]; at != root; at = parent[at])
        {
            pieces.Add(at);
        }

        pieces.Reverse();
        long? room = nodes[device].Room;
        List<long> rooms = new List<long>();
        List<long> through = new List<long>();
        foreach (long piece in pieces)
        {
            long? at = nodes[piece].Room;
            if (!at.HasValue || (rooms.Count > 0 && rooms[rooms.Count - 1] == at.Value))
            {
                continue;
            }

            rooms.Add(at.Value);
            if (at != rootRoom && at != room && !through.Contains(at.Value))
            {
                through.Add(at.Value);
            }
        }

        return new FeedPath(device, room, pieces, rooms, through, EntryOf(pieces, room, nodes));
    }

    // Walking back from the device over pieces in its room or in none, the earliest one in its room.
    private static long? EntryOf(List<long> pieces, long? room, IReadOnlyDictionary<long, FeedNode> nodes)
    {
        if (!room.HasValue)
        {
            return null;
        }

        long? entry = null;
        for (int index = pieces.Count - 1; index >= 0; index--)
        {
            long? at = nodes[pieces[index]].Room;
            if (at.HasValue && at.Value != room.Value)
            {
                break;
            }

            if (at.HasValue)
            {
                entry = pieces[index];
            }
        }

        return entry ?? (pieces.Count > 0 ? pieces[pieces.Count - 1] : (long?)null);
    }

    private static List<RoomFeed> RoomsOf(List<FeedPath> paths)
    {
        List<RoomFeed> rooms = new List<RoomFeed>();
        Dictionary<long, RoomFeed> byRoom = new Dictionary<long, RoomFeed>();
        foreach (FeedPath path in paths)
        {
            if (!path.Room.HasValue)
            {
                continue;
            }

            if (!byRoom.TryGetValue(path.Room.Value, out RoomFeed feed))
            {
                feed = new RoomFeed(path.Room.Value, new List<long>(), new List<long>());
                byRoom[path.Room.Value] = feed;
                rooms.Add(feed);
            }

            feed.Devices.Add(path.Device);
            if (path.Entry.HasValue && !feed.Entries.Contains(path.Entry.Value))
            {
                feed.Entries.Add(path.Entry.Value);
            }
        }

        return rooms;
    }
}
