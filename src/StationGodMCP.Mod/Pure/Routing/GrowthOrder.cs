#nullable enable

using System;
using System.Collections.Generic;

namespace StationGodMCP.Pure;

/// <summary>
/// The order a run's new pieces are built in so that each joins what already stands. A piece the game registers
/// with no connected neighbour gets a network of its own (Pipe.OnRegistered: new PipeNetwork), and the piece that
/// later joins it to a standing network merges the two into whichever it met first (StructureNetwork.Merge), so the
/// standing network may be merged away and its id lost.
/// A joined client fares worse. It builds a state packet's new pieces in the host's order, but each names the network
/// it is in when the packet is written, and a piece with no connected neighbour on the client goes straight into that
/// network (Pipe.DeserializeOnJoin). When the host merges a lone piece's network into a standing network and later
/// merges that standing network into another lone piece's network, the client has put the first lone piece into the
/// last one's network and merges it into the standing network: the client loses a network the host keeps. Every delta
/// the host sends for it then throws (StructureNetwork.DeserializeDeltaState), and the next piece that names it throws
/// in DeserializeOnJoin and loses the rest of that packet. Grown outward from every standing piece the run touches, a
/// piece stands alone only where its part of the run touches nothing standing, and then it is the only lone piece of
/// that part: the network it starts is the one every piece of the part ends in, on the host and on every client.
/// </summary>
internal static class GrowthOrder
{
    /// <summary>
    /// The pieces, each next one the first (in the given order) that connects to what stands or is built so far, by
    /// the game's own connection rule (Connectivity.Links, either way). When none connects, the first left starts a new
    /// part, which then grows the same way.
    /// </summary>
    internal static List<T> From<T>(IReadOnlyList<PieceModel> standing, IReadOnlyList<T> pieces,
        Func<T, PieceModel> modelOf)
    {
        List<PieceModel> models = new List<PieceModel>(pieces.Count);
        for (int index = 0; index < pieces.Count; index++)
        {
            models.Add(modelOf(pieces[index]));
        }

        PieceLinks links = new PieceLinks(models, standing);
        SortedSet<int> frontier = new SortedSet<int>();
        for (int index = 0; index < models.Count; index++)
        {
            if (links.TouchesStanding(index))
            {
                frontier.Add(index);
            }
        }

        bool[] placed = new bool[models.Count];
        List<T> ordered = new List<T>(pieces.Count);
        int firstLeft = 0;
        while (ordered.Count < pieces.Count)
        {
            int next;
            if (frontier.Count > 0)
            {
                next = frontier.Min;
                frontier.Remove(next);
            }
            else
            {
                while (placed[firstLeft])
                {
                    firstLeft++;
                }

                next = firstLeft;
            }

            placed[next] = true;
            ordered.Add(pieces[next]);
            foreach (int neighbour in links.Of(next))
            {
                if (!placed[neighbour])
                {
                    frontier.Add(neighbour);
                }
            }
        }

        return ordered;
    }

    // Which pieces link (Connectivity.Links, either way), looked up by cell. Links(a, b) needs b to occupy the cell an
    // end of a sits in, so a's linked pieces are those occupying one of its end cells and those with an end in one of
    // its own cells.
    private sealed class PieceLinks
    {
        private readonly List<PieceModel> _models;
        private readonly CellIndex<int> _pieces = new CellIndex<int>();
        private readonly CellIndex<PieceModel> _standing = new CellIndex<PieceModel>();

        internal PieceLinks(List<PieceModel> models, IReadOnlyList<PieceModel> standing)
        {
            _models = models;
            for (int index = 0; index < models.Count; index++)
            {
                _pieces.Add(models[index], index);
            }

            foreach (PieceModel piece in standing)
            {
                _standing.Add(piece, piece);
            }
        }

        internal bool TouchesStanding(int index)
        {
            PieceModel piece = _models[index];
            foreach (PieceModel other in _standing.Near(piece))
            {
                if (Linked(piece, other))
                {
                    return true;
                }
            }

            return false;
        }

        internal List<int> Of(int index)
        {
            PieceModel piece = _models[index];
            List<int> found = new List<int>();
            foreach (int other in _pieces.Near(piece))
            {
                if (other != index && !found.Contains(other) && Linked(piece, _models[other]))
                {
                    found.Add(other);
                }
            }

            return found;
        }

        private static bool Linked(PieceModel a, PieceModel b) => Connectivity.Links(a, b) || Connectivity.Links(b, a);
    }

    private sealed class CellIndex<TItem>
    {
        private readonly Dictionary<GridCell, List<TItem>> _occupying = new Dictionary<GridCell, List<TItem>>();
        private readonly Dictionary<GridCell, List<TItem>> _endsIn = new Dictionary<GridCell, List<TItem>>();

        internal void Add(PieceModel piece, TItem item)
        {
            foreach (GridCell cell in piece.Cells)
            {
                Put(_occupying, cell, item);
            }

            foreach (PieceEnd end in piece.Ends)
            {
                Put(_endsIn, end.Local, item);
            }
        }

        internal List<TItem> Near(PieceModel piece)
        {
            List<TItem> near = new List<TItem>();
            foreach (PieceEnd end in piece.Ends)
            {
                if (_occupying.TryGetValue(end.Local, out List<TItem> there))
                {
                    near.AddRange(there);
                }
            }

            foreach (GridCell cell in piece.Cells)
            {
                if (_endsIn.TryGetValue(cell, out List<TItem> ending))
                {
                    near.AddRange(ending);
                }
            }

            return near;
        }

        private static void Put(Dictionary<GridCell, List<TItem>> index, GridCell cell, TItem item)
        {
            if (!index.TryGetValue(cell, out List<TItem> list))
            {
                list = new List<TItem>();
                index[cell] = list;
            }

            list.Add(item);
        }
    }
}
