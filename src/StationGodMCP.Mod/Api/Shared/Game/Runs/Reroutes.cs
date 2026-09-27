#nullable enable

using System.Collections.Generic;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Pipes;
using StationGodMCP.Api.Shared.Game.Upgrades;
using StationGodMCP.Pure;

namespace StationGodMCP.Api.Shared.Game.Runs;

/// <summary>An old run to replace: its pieces, and the two cells where it meets the rest of the world.</summary>
internal sealed class RerouteSegment
{
    internal RerouteSegment(List<SmallGrid> pieces, GridCell first, GridCell last)
    {
        Pieces = pieces;
        First = first;
        Last = last;
    }

    internal List<SmallGrid> Pieces { get; }

    internal GridCell First { get; }

    internal GridCell Last { get; }
}

/// <summary>
/// The old run a reroute replaces: listed pieces, or the pieces of the shortest way through one network between two
/// things on it (devices or pieces, by the model's links). It must meet everything else at exactly two cells, each a
/// cell of the old run with an end joining something outside it; the new route runs between those two cells (free
/// once the old pieces are gone) and its ends join the same things again. A run with branches or a dead end is
/// refused: name a segment without them.
/// </summary>
internal static class Reroutes
{
    private const int MaximumPieces = 1024;

    internal static RerouteSegment OfPieces(RunKind kind, List<ThingId> ids)
    {
        List<SmallGrid> pieces = new List<SmallGrid>(ids.Count);
        foreach (ThingId id in ids)
        {
            Thing thing = GameLookup.RequireThing(id);
            if (!(thing is SmallGrid piece) || !kind.Family.IsPiece(piece))
            {
                throw ApiErrors.InvalidArgument($"{thing.DisplayName} ({thing.PrefabName}) is not a {kind.Noun} piece.");
            }

            if (!pieces.Contains(piece))
            {
                pieces.Add(piece);
            }
        }

        return Bounded(pieces);
    }

    internal static RerouteSegment Between(RunKind kind, ThingId from, ThingId to)
    {
        if (!(GameLookup.RequireThing(from) is SmallGrid a) || !(GameLookup.RequireThing(to) is SmallGrid b))
        {
            throw ApiErrors.InvalidArgument("between names two devices or pieces.");
        }

        long? network = NetworkNear(kind, a);
        if (network == null || NetworkNear(kind, b) != network)
        {
            throw ApiErrors.Refused("not_on_one_network",
                $"{a.PrefabName} {a.ReferenceId} and {b.PrefabName} {b.ReferenceId} are not on one {kind.Noun} " +
                "network (a device on several networks of the kind cannot say which).");
        }

        List<SmallGrid> members = kind.Family.NetworkMembers(new ThingId(network.Value));
        if (members.Count > 4 * MaximumPieces)
        {
            throw ApiErrors.Refused("too_many_pieces", $"Network {network.Value} has {members.Count} pieces.");
        }

        Dictionary<long, PieceModel> models = new Dictionary<long, PieceModel>();
        foreach (SmallGrid member in members)
        {
            models[member.ReferenceId] = PieceShapes.Live(member);
        }

        List<PieceModel> all = new List<PieceModel>(models.Values);
        if (!models.ContainsKey(a.ReferenceId))
        {
            all.Add(PieceShapes.Live(a));
        }

        if (!models.ContainsKey(b.ReferenceId))
        {
            all.Add(PieceShapes.Live(b));
        }

        HashSet<Link> links = Connectivity.LinksTouching(all, new HashSet<long>(models.Keys));
        List<long> path = ShortestPath(links, a.ReferenceId, b.ReferenceId, models);
        List<SmallGrid> pieces = new List<SmallGrid>();
        foreach (long id in path)
        {
            if (models.ContainsKey(id) && kind.Family.IsPiece(Find(members, id)))
            {
                pieces.Add(Find(members, id));
            }
        }

        if (pieces.Count == 0)
        {
            throw ApiErrors.Refused("no_run_between",
                $"No run of {kind.Noun} pieces joins {a.ReferenceId} and {b.ReferenceId} by the model's links.");
        }

        return Bounded(pieces);
    }

    private static SmallGrid Find(List<SmallGrid> members, long id) => members.Find(member => member.ReferenceId == id);

    private static long? NetworkNear(RunKind kind, SmallGrid thing)
    {
        if (kind.Family.IsMember(thing))
        {
            return kind.Family.NetworkOf(thing)?.ReferenceId;
        }

        if (thing is Device device)
        {
            List<long> networks = kind.Family.DeviceNetworks(device);
            return networks.Count == 1 ? networks[0] : (long?)null;
        }

        return null;
    }

    // Breadth first over the links between the two ids, through pieces only.
    private static List<long> ShortestPath(HashSet<Link> links, long from, long to, Dictionary<long, PieceModel> pieces)
    {
        Dictionary<long, List<long>> next = new Dictionary<long, List<long>>();
        foreach (Link link in links)
        {
            Add(next, link.From, link.To);
            Add(next, link.To, link.From);
        }

        Dictionary<long, long> cameFrom = new Dictionary<long, long>();
        Queue<long> queue = new Queue<long>();
        queue.Enqueue(from);
        cameFrom[from] = from;
        while (queue.Count > 0)
        {
            long at = queue.Dequeue();
            if (at == to)
            {
                break;
            }

            if (!next.TryGetValue(at, out List<long> around))
            {
                continue;
            }

            foreach (long neighbour in around)
            {
                if (!cameFrom.ContainsKey(neighbour) && (neighbour == to || pieces.ContainsKey(neighbour)))
                {
                    cameFrom[neighbour] = at;
                    queue.Enqueue(neighbour);
                }
            }
        }

        List<long> path = new List<long>();
        if (!cameFrom.ContainsKey(to))
        {
            return path;
        }

        for (long at = to; at != from; at = cameFrom[at])
        {
            path.Add(at);
        }

        path.Add(from);
        path.Reverse();
        return path;
    }

    private static void Add(Dictionary<long, List<long>> next, long a, long b)
    {
        if (!next.TryGetValue(a, out List<long> list))
        {
            list = new List<long>();
            next[a] = list;
        }

        list.Add(b);
    }

    // The cells where the segment's pieces link to something outside it: exactly two.
    private static RerouteSegment Bounded(List<SmallGrid> pieces)
    {
        if (pieces.Count > MaximumPieces)
        {
            throw ApiErrors.Refused("too_many_pieces", $"{pieces.Count} pieces; at most {MaximumPieces}.");
        }

        HashSet<long> inside = new HashSet<long>();
        List<PieceModel> models = new List<PieceModel>(pieces.Count);
        foreach (SmallGrid piece in pieces)
        {
            inside.Add(piece.ReferenceId);
            models.Add(PieceShapes.Live(piece));
        }

        List<PieceModel> outside = new List<PieceModel>();
        foreach (SmallGrid thing in LinkSurvey.Neighbourhood(models).Values)
        {
            if (!inside.Contains(thing.ReferenceId))
            {
                outside.Add(PieceShapes.Live(thing));
            }
        }

        List<GridCell> cells = new List<GridCell>();
        foreach (PieceModel model in models)
        {
            foreach (PieceEnd end in model.Ends)
            {
                foreach (PieceModel other in outside)
                {
                    bool joined = other.Occupies(end.Local) && Connectivity.Links(model, other);
                    if (joined && !cells.Contains(end.Facing))
                    {
                        cells.Add(end.Facing);
                    }
                }
            }
        }

        if (cells.Count != 2)
        {
            throw ApiErrors.Refused("not_a_segment",
                $"The pieces meet the rest of the world at {cells.Count} cell(s) " +
                $"[{string.Join(", ", cells.ConvertAll(static cell => cell.ToString()))}], not two: a reroute " +
                "replaces a run with one way in and one way out. Name a segment without branches or dead ends.");
        }

        return new RerouteSegment(pieces, cells[0], cells[1]);
    }
}
