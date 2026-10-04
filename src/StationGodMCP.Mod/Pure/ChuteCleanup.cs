#nullable enable

using System;
using System.Collections.Generic;

namespace StationGodMCP.Pure;

/// <summary>Why no item reaches a consumer through a dead chute piece.</summary>
internal enum DeadChuteReason
{
    /// <summary>Joined to nothing at all.</summary>
    Orphan,

    /// <summary>Items that enter it can reach no device that takes them.</summary>
    NoConsumer,

    /// <summary>It leads to a consumer, but nothing ever puts an item into it.</summary>
    NoSource
}

/// <summary>The reply's names for the reasons and for what keeps a dead piece in place.</summary>
internal static class ChuteCleanupNames
{
    internal const string KeepIds = "keep_ids";
    internal const string ItemRiding = "item_riding";
    internal const string WouldDropItems = "would_drop_items";
    internal const string NoPlainPiece = "no_plain_piece";

    internal static string Of(DeadChuteReason reason) =>
        reason switch
        {
            DeadChuteReason.Orphan => "orphan",
            DeadChuteReason.NoConsumer => "no_consumer",
            DeadChuteReason.NoSource => "no_source",
            _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null)
        };
}

/// <summary>
/// The chute networks clean_chutes judges, on plain values: every piece of them (not only the selected ones, since a
/// piece is dead or alive by what the whole network does), the device chute ports, which pieces may change, why
/// some may not (keep_ids, an item riding in it, indestructible, rocket, no kit), which pieces carry an item now,
/// which devices let items fall out of a port left open, and whether a plain piece with given ends exists.
/// </summary>
internal sealed class ChuteCleanupScene
{
    internal ChuteCleanupScene(IReadOnlyList<PieceModel> pieces, IReadOnlyList<DevicePort> ports,
        IReadOnlyCollection<long> selected, IReadOnlyDictionary<long, string> held, IReadOnlyCollection<long> riding,
        IReadOnlyCollection<long> outletDevices, Func<PieceModel, bool> hasPlainPiece)
    {
        Pieces = pieces;
        Ports = ports;
        Selected = new HashSet<long>(selected);
        Held = held;
        Riding = new HashSet<long>(riding);
        OutletDevices = new HashSet<long>(outletDevices);
        HasPlainPiece = hasPlainPiece;
    }

    internal IReadOnlyList<PieceModel> Pieces { get; }

    /// <summary>Every chute port of every device on the networks; End.Role as ChuteRoles.OfDevicePort gives it.</summary>
    internal IReadOnlyList<DevicePort> Ports { get; }

    /// <summary>The pieces the request names: only they may be removed or replaced.</summary>
    internal HashSet<long> Selected { get; }

    /// <summary>Selected pieces that may not change, with the reason.</summary>
    internal IReadOnlyDictionary<long, string> Held { get; }

    /// <summary>Pieces with an item in their transport slot now: items will leave them, so they are sources.</summary>
    internal HashSet<long> Riding { get; }

    /// <summary>
    /// Devices that move items along the chute themselves (chute digital valves and splitters, umbilicals) and drop
    /// them out of a port nothing is joined to.
    /// </summary>
    internal HashSet<long> OutletDevices { get; }

    /// <summary>Whether the piece's kit places a piece with exactly this model (cells and role-free ends).</summary>
    internal Func<PieceModel, bool> HasPlainPiece { get; }
}

/// <summary>What clean_chutes decides for one selected piece.</summary>
internal abstract class ChuteVerdict
{
    private protected ChuteVerdict(PieceModel piece)
    {
        Piece = piece;
    }

    internal PieceModel Piece { get; }

    internal long Id => Piece.Id;
}

/// <summary>Items can reach a consumer through it, and it keeps every end: it stays as it is.</summary>
internal sealed class LiveChute : ChuteVerdict
{
    internal LiveChute(PieceModel piece)
        : base(piece)
    {
    }
}

/// <summary>Serves no path: removed.</summary>
internal sealed class RemovedChute : ChuteVerdict
{
    internal RemovedChute(PieceModel piece, DeadChuteReason reason)
        : base(piece)
    {
        Reason = reason;
    }

    internal DeadChuteReason Reason { get; }
}

/// <summary>Serves no path but stays: held (keep_ids, an item, ...) or kept so nothing drops items where it was.</summary>
internal sealed class KeptDeadChute : ChuteVerdict
{
    internal KeptDeadChute(PieceModel piece, DeadChuteReason reason, string heldBy, string? detail)
        : base(piece)
    {
        Reason = reason;
        HeldBy = heldBy;
        Detail = detail;
    }

    internal DeadChuteReason Reason { get; }

    internal string HeldBy { get; }

    internal string? Detail { get; }
}

/// <summary>
/// A junction, overflow or splitter on a path that is left with one end taking items in and one letting them out:
/// it becomes the plain piece (straight or corner) with those two ends.
/// </summary>
internal sealed class ReducedChute : ChuteVerdict
{
    internal ReducedChute(PieceModel piece, PieceModel plain, IReadOnlyList<PieceEnd> connected)
        : base(piece)
    {
        Plain = plain;
        Connected = connected;
    }

    /// <summary>The replacement's model: the old cells and the two ends, with no role.</summary>
    internal PieceModel Plain { get; }

    /// <summary>The old piece's ends that stay joined, as they are on it.</summary>
    internal IReadOnlyList<PieceEnd> Connected { get; }
}

/// <summary>A piece on a path that keeps an end leading nowhere, and why it could not become a plain piece.</summary>
internal sealed class OpenEndedChute : ChuteVerdict
{
    internal OpenEndedChute(PieceModel piece, string heldBy)
        : base(piece)
    {
        HeldBy = heldBy;
    }

    internal string HeldBy { get; }
}

/// <summary>Every selected piece's verdict, in the order the scene lists the pieces.</summary>
internal sealed class ChuteCleanupResult
{
    internal ChuteCleanupResult(List<ChuteVerdict> verdicts)
    {
        Verdicts = verdicts;
    }

    internal List<ChuteVerdict> Verdicts { get; }
}

/// <summary>
/// clean_chutes' rule. Items move one way through a chute network (ChuteFlow). A piece serves a path when an item can
/// get into it from a source and on from it to a consumer:
/// sources are device ports that push items in (role Output, and two-way ports) and pieces an item rides in now;
/// consumers are device ports that take items (role Input, and two-way ports). An open end is neither: an item
/// leaving through it falls to the floor, which is a leak, not a destination.
/// Every selected piece that serves no path is dead and removed, unless it is held (keep_ids, an item riding in it,
/// indestructible, rocket, no kit) or removing it would let items fall out of something that stays: a piece or a
/// chute device whose end into it lets items out (or has no known direction). Such a dead piece stays (
/// would_drop_items), and so, in turn, may the dead pieces behind it. The one exception is a junction, overflow or
/// splitter on a path: when the dead branch leaves it with exactly one end taking items in and one letting them out,
/// it becomes the plain piece with those two ends in the same run, so it opens no end. A junction, overflow or
/// splitter on a path with an end that already leads nowhere becomes a plain piece the same way.
/// </summary>
internal static class ChuteCleanup
{
    internal static ChuteCleanupResult Plan(ChuteCleanupScene scene)
    {
        Graph graph = Graph.Of(scene);
        bool[] fromSource = graph.Reach(graph.Sources(scene), forward: true);
        bool[] toConsumer = graph.Reach(graph.Consumers(), forward: false);
        int count = scene.Pieces.Count;
        bool[] live = new bool[count];
        bool[] removing = new bool[count];
        Dictionary<int, string> pinned = new Dictionary<int, string>();
        for (int index = 0; index < count; index++)
        {
            live[index] = fromSource[index] && toConsumer[index];
            long id = scene.Pieces[index].Id;
            removing[index] = !live[index] && scene.Selected.Contains(id) && !scene.Held.ContainsKey(id);
        }

        Dictionary<int, ReducedChute> reductions = Settle(scene, graph, live, removing, pinned);
        List<ChuteVerdict> verdicts = new List<ChuteVerdict>();
        for (int index = 0; index < count; index++)
        {
            PieceModel piece = scene.Pieces[index];
            if (!scene.Selected.Contains(piece.Id))
            {
                continue;
            }

            verdicts.Add(VerdictOf(scene, graph, index, live, removing, pinned, reductions,
                ReasonOf(graph, index, toConsumer[index])));
        }

        return new ChuteCleanupResult(verdicts);
    }

    private static ChuteVerdict VerdictOf(ChuteCleanupScene scene, Graph graph, int index, bool[] live,
        bool[] removing, Dictionary<int, string> pinned, Dictionary<int, ReducedChute> reductions,
        DeadChuteReason reason)
    {
        PieceModel piece = scene.Pieces[index];
        if (removing[index])
        {
            return new RemovedChute(piece, reason);
        }

        if (!live[index])
        {
            return scene.Held.TryGetValue(piece.Id, out string heldBy)
                ? new KeptDeadChute(piece, reason, heldBy, null)
                : new KeptDeadChute(piece, reason, ChuteCleanupNames.WouldDropItems, pinned[index]);
        }

        if (reductions.TryGetValue(index, out ReducedChute reduced))
        {
            return reduced;
        }

        return IsDirected(piece) && graph.HasOpenEnd(index, removing)
            ? new OpenEndedChute(piece, OpenEndCause(scene, piece))
            : new LiveChute(piece);
    }

    private static string OpenEndCause(ChuteCleanupScene scene, PieceModel piece) =>
        scene.Held.TryGetValue(piece.Id, out string heldBy) ? heldBy : ChuteCleanupNames.NoPlainPiece;

    private static DeadChuteReason ReasonOf(Graph graph, int index, bool toConsumer) =>
        !graph.IsJoined(index) ? DeadChuteReason.Orphan
        : !toConsumer ? DeadChuteReason.NoConsumer
        : DeadChuteReason.NoSource;

    // Until nothing changes: every piece that stays and could let items out into a piece being removed either becomes
    // a plain piece without that end, or keeps the piece behind it (which then stays too, and is looked at in turn).
    private static Dictionary<int, ReducedChute> Settle(ChuteCleanupScene scene, Graph graph, bool[] live,
        bool[] removing, Dictionary<int, string> pinned)
    {
        Dictionary<int, ReducedChute> reductions = new Dictionary<int, ReducedChute>();
        bool changed = true;
        while (changed)
        {
            changed = false;
            reductions.Clear();
            for (int index = 0; index < scene.Pieces.Count; index++)
            {
                if (removing[index] || !graph.LetsOutInto(index, removing, out int into))
                {
                    continue;
                }

                ReducedChute? reduced = live[index] ? Reduce(scene, graph, index, removing) : null;
                if (reduced != null)
                {
                    reductions[index] = reduced;
                    continue;
                }

                removing[into] = false;
                pinned[into] = $"piece {scene.Pieces[index].Id} would let items fall out where it was";
                changed = true;
            }

            foreach (KeyValuePair<int, long> outlet in graph.DevicesLettingOutInto(removing))
            {
                removing[outlet.Key] = false;
                pinned[outlet.Key] = $"device {outlet.Value} would let items fall out where it was";
                changed = true;
            }
        }

        for (int index = 0; index < scene.Pieces.Count; index++)
        {
            if (live[index] && !reductions.ContainsKey(index) && graph.HasOpenEnd(index, removing))
            {
                ReducedChute? reduced = Reduce(scene, graph, index, removing);
                if (reduced != null)
                {
                    reductions[index] = reduced;
                }
            }
        }

        return reductions;
    }

    // A selected, free junction, overflow or splitter left with one end taking items in and one letting them out,
    // when its kit places the plain piece with those two ends.
    private static ReducedChute? Reduce(ChuteCleanupScene scene, Graph graph, int index, bool[] removing)
    {
        PieceModel piece = scene.Pieces[index];
        if (!scene.Selected.Contains(piece.Id) || scene.Held.ContainsKey(piece.Id) || piece.Ends.Count < 3 ||
            !IsDirected(piece))
        {
            return null;
        }

        List<PieceEnd> connected = graph.ConnectedEnds(index, removing);
        if (connected.Count != 2 || !(ChuteRoles.TakesIn(connected[0].Role) && ChuteRoles.LetsOut(connected[1].Role)
                                      || ChuteRoles.LetsOut(connected[0].Role) &&
                                      ChuteRoles.TakesIn(connected[1].Role)))
        {
            return null;
        }

        PieceModel plain = PlainOf(piece, connected);
        return scene.HasPlainPiece(plain) ? new ReducedChute(piece, plain, connected) : null;
    }

    /// <summary>The piece's cells with only the given ends, each without a role (a straight's or corner's ends).</summary>
    internal static PieceModel PlainOf(PieceModel piece, IReadOnlyList<PieceEnd> ends)
    {
        List<PieceEnd> plain = new List<PieceEnd>(ends.Count);
        foreach (PieceEnd end in ends)
        {
            plain.Add(new PieceEnd(end.Local, end.Facing, end.Type, ChuteRoles.None));
        }

        return new PieceModel(piece.Id, piece.Cells, plain, piece.Content);
    }

    private static bool IsDirected(PieceModel piece)
    {
        foreach (PieceEnd end in piece.Ends)
        {
            if (ChuteRoles.IsDirected(end.Role))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>What each end of each piece is joined to, the flow across it, and the piece-to-piece moves.</summary>
    private sealed class Graph
    {
        private readonly ChuteCleanupScene _scene;
        private readonly ChuteFlowResult _flow;

        // Per piece, per end: the other piece's index (-1 none) and the device port's index in scene.Ports (-1 none).
        private readonly int[][] _pieceAt;
        private readonly int[][] _portAt;
        private readonly List<int>[] _next;
        private readonly List<int>[] _previous;

        private Graph(ChuteCleanupScene scene, ChuteFlowResult flow, int[][] pieceAt, int[][] portAt)
        {
            _scene = scene;
            _flow = flow;
            _pieceAt = pieceAt;
            _portAt = portAt;
            int count = scene.Pieces.Count;
            _next = new List<int>[count];
            _previous = new List<int>[count];
            for (int index = 0; index < count; index++)
            {
                _next[index] = new List<int>(2);
                _previous[index] = new List<int>(2);
            }
        }

        internal static Graph Of(ChuteCleanupScene scene)
        {
            ChuteFlowResult flow = ChuteFlow.Solve(new ChuteFlowGraph(scene.Pieces, scene.Ports, new long[0],
                new List<RunLeg>()));
            Dictionary<GridCell, List<int>> byCell = ByCell(scene.Pieces);
            Dictionary<GridCell, List<int>> portsByCell = PortsByCell(scene.Ports);
            int count = scene.Pieces.Count;
            int[][] pieceAt = new int[count][];
            int[][] portAt = new int[count][];
            for (int index = 0; index < count; index++)
            {
                PieceModel piece = scene.Pieces[index];
                pieceAt[index] = new int[piece.Ends.Count];
                portAt[index] = new int[piece.Ends.Count];
                for (int end = 0; end < piece.Ends.Count; end++)
                {
                    pieceAt[index][end] = JoinedPiece(scene, byCell, index, piece.Ends[end]);
                    portAt[index][end] = JoinedPort(scene, portsByCell, piece.Ends[end]);
                }
            }

            Graph graph = new Graph(scene, flow, pieceAt, portAt);
            graph.Link();
            return graph;
        }

        // A move from one piece into the other wherever the flow does not say items go the other way only.
        private void Link()
        {
            for (int index = 0; index < _scene.Pieces.Count; index++)
            {
                PieceModel piece = _scene.Pieces[index];
                for (int end = 0; end < piece.Ends.Count; end++)
                {
                    int other = _pieceAt[index][end];
                    if (other < 0)
                    {
                        continue;
                    }

                    if (MayLetOut(index, end))
                    {
                        _next[index].Add(other);
                        _previous[other].Add(index);
                    }
                }
            }
        }

        // Out, or no known direction: items may leave the piece through this end.
        private bool MayLetOut(int index, int end) =>
            _flow.Of(_scene.Pieces[index].Id, end) != FlowDirection.In;

        internal List<int> Sources(ChuteCleanupScene scene)
        {
            List<int> sources = new List<int>();
            for (int index = 0; index < scene.Pieces.Count; index++)
            {
                if (scene.Riding.Contains(scene.Pieces[index].Id) || JoinsPort(index, pushesIn: true))
                {
                    sources.Add(index);
                }
            }

            return sources;
        }

        internal List<int> Consumers()
        {
            List<int> consumers = new List<int>();
            for (int index = 0; index < _scene.Pieces.Count; index++)
            {
                if (JoinsPort(index, pushesIn: false))
                {
                    consumers.Add(index);
                }
            }

            return consumers;
        }

        // A port pushes items into the chute unless it only takes them; it takes them unless it only pushes them.
        private bool JoinsPort(int index, bool pushesIn)
        {
            foreach (int port in _portAt[index])
            {
                if (port < 0)
                {
                    continue;
                }

                int role = _scene.Ports[port].End.Role;
                if (pushesIn ? !ChuteRoles.TakesIn(role) : !ChuteRoles.LetsOut(role))
                {
                    return true;
                }
            }

            return false;
        }

        internal bool[] Reach(List<int> starts, bool forward)
        {
            bool[] reached = new bool[_scene.Pieces.Count];
            Queue<int> queue = new Queue<int>(starts.Count);
            foreach (int start in starts)
            {
                if (!reached[start])
                {
                    reached[start] = true;
                    queue.Enqueue(start);
                }
            }

            while (queue.Count > 0)
            {
                foreach (int next in (forward ? _next : _previous)[queue.Dequeue()])
                {
                    if (!reached[next])
                    {
                        reached[next] = true;
                        queue.Enqueue(next);
                    }
                }
            }

            return reached;
        }

        internal bool IsJoined(int index)
        {
            for (int end = 0; end < _pieceAt[index].Length; end++)
            {
                if (_pieceAt[index][end] >= 0 || _portAt[index][end] >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Whether an end of the piece that may let items out is joined to a piece being removed.</summary>
        internal bool LetsOutInto(int index, bool[] removing, out int into)
        {
            for (int end = 0; end < _pieceAt[index].Length; end++)
            {
                int other = _pieceAt[index][end];
                if (other >= 0 && removing[other] && MayLetOut(index, end))
                {
                    into = other;
                    return true;
                }
            }

            into = -1;
            return false;
        }

        /// <summary>Each piece being removed that a chute device's port lets items out into, with the device's id.</summary>
        internal List<KeyValuePair<int, long>> DevicesLettingOutInto(bool[] removing)
        {
            List<KeyValuePair<int, long>> outlets = new List<KeyValuePair<int, long>>();
            for (int index = 0; index < _scene.Pieces.Count; index++)
            {
                if (!removing[index])
                {
                    continue;
                }

                foreach (int port in _portAt[index])
                {
                    if (port >= 0 && _scene.OutletDevices.Contains(_scene.Ports[port].DeviceId) &&
                        !ChuteRoles.TakesIn(_scene.Ports[port].End.Role))
                    {
                        outlets.Add(new KeyValuePair<int, long>(index, _scene.Ports[port].DeviceId));
                        break;
                    }
                }
            }

            return outlets;
        }

        /// <summary>The piece's ends joined to a piece that stays or to a device port.</summary>
        internal List<PieceEnd> ConnectedEnds(int index, bool[] removing)
        {
            PieceModel piece = _scene.Pieces[index];
            List<PieceEnd> connected = new List<PieceEnd>(piece.Ends.Count);
            for (int end = 0; end < piece.Ends.Count; end++)
            {
                int other = _pieceAt[index][end];
                if (_portAt[index][end] >= 0 || other >= 0 && !removing[other])
                {
                    connected.Add(piece.Ends[end]);
                }
            }

            return connected;
        }

        internal bool HasOpenEnd(int index, bool[] removing) =>
            ConnectedEnds(index, removing).Count < _scene.Pieces[index].Ends.Count;

        private static int JoinedPiece(ChuteCleanupScene scene, Dictionary<GridCell, List<int>> byCell, int index,
            PieceEnd end)
        {
            if (!byCell.TryGetValue(end.Local, out List<int> there))
            {
                return -1;
            }

            foreach (int other in there)
            {
                if (other != index && Matches(scene.Pieces[other].Ends, end))
                {
                    return other;
                }
            }

            return -1;
        }

        // The other's end that joins this one sits in this end's facing cell, faces this end's cell, shares a type.
        private static bool Matches(IReadOnlyList<PieceEnd> ends, PieceEnd end)
        {
            foreach (PieceEnd theirs in ends)
            {
                if ((theirs.Type & end.Type) != 0 && theirs.Local.Equals(end.Facing) && theirs.Facing.Equals(end.Local))
                {
                    return true;
                }
            }

            return false;
        }

        // A port's End.Local is the cell the piece joining it stands in, which is that piece end's facing cell.
        private static int JoinedPort(ChuteCleanupScene scene, Dictionary<GridCell, List<int>> portsByCell,
            PieceEnd end)
        {
            if (!portsByCell.TryGetValue(end.Facing, out List<int> there))
            {
                return -1;
            }

            foreach (int port in there)
            {
                PieceEnd theirs = scene.Ports[port].End;
                if ((theirs.Type & end.Type) != 0 && theirs.Facing.Equals(end.Local))
                {
                    return port;
                }
            }

            return -1;
        }

        private static Dictionary<GridCell, List<int>> PortsByCell(IReadOnlyList<DevicePort> ports)
        {
            Dictionary<GridCell, List<int>> byCell = new Dictionary<GridCell, List<int>>();
            for (int index = 0; index < ports.Count; index++)
            {
                if (!byCell.TryGetValue(ports[index].End.Local, out List<int> list))
                {
                    list = new List<int>(1);
                    byCell[ports[index].End.Local] = list;
                }

                list.Add(index);
            }

            return byCell;
        }

        private static Dictionary<GridCell, List<int>> ByCell(IReadOnlyList<PieceModel> pieces)
        {
            Dictionary<GridCell, List<int>> byCell = new Dictionary<GridCell, List<int>>();
            for (int index = 0; index < pieces.Count; index++)
            {
                foreach (GridCell cell in pieces[index].Cells)
                {
                    if (!byCell.TryGetValue(cell, out List<int> list))
                    {
                        list = new List<int>(1);
                        byCell[cell] = list;
                    }

                    list.Add(index);
                }
            }

            return byCell;
        }
    }
}
