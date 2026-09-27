#nullable enable

using System.Collections.Generic;

namespace StationGodMCP.Pure;

/// <summary>What pruning found: each removed piece with its round, and each dead end left in place, with why.</summary>
internal sealed class PruneResult
{
    internal PruneResult(List<KeyValuePair<long, int>> removed, Dictionary<long, string> stops)
    {
        Removed = removed;
        Stops = stops;
    }

    /// <summary>Removed piece ids with the round (1, 2, ...) each goes in, in the order they were found.</summary>
    internal List<KeyValuePair<long, int>> Removed { get; }

    /// <summary>Dead ends left in place, with the reason.</summary>
    internal Dictionary<long, string> Stops { get; }

    internal bool IsRemoved(long id)
    {
        foreach (KeyValuePair<long, int> entry in Removed)
        {
            if (entry.Key == id)
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// remove_dead_ends on plain values. A candidate with one connected end (a stub) or none (isolated) is removed; the
/// pieces it was connected to are looked at again, since removing a stub can make its neighbour one, and so on in
/// rounds until no new stub appears. A stub whose connected end reaches a device stays (device_connected) and so the
/// run behind it stays too; a candidate the caller blocks (a device mounted on it, a network that would be emptied
/// with contents in it) stays with the caller's reason. Only candidates are ever removed; other models (devices,
/// pieces outside the selection) only connect.
/// </summary>
internal static class DeadEndPruning
{
    internal const string DeviceConnected = "device_connected";

    internal static PruneResult Prune(IReadOnlyList<PieceModel> candidates, IReadOnlyList<PieceModel> others,
        HashSet<long> devices, IReadOnlyDictionary<long, string> blocked)
    {
        Dictionary<GridCell, List<PieceModel>> byCell = IndexByCell(candidates, others);
        HashSet<long> removed = new HashSet<long>();
        List<KeyValuePair<long, int>> order = new List<KeyValuePair<long, int>>();
        Dictionary<long, string> stops = new Dictionary<long, string>();
        List<PieceModel> look = new List<PieceModel>(candidates);
        HashSet<long> candidateIds = new HashSet<long>();
        foreach (PieceModel candidate in candidates)
        {
            candidateIds.Add(candidate.Id);
        }

        for (int round = 1; look.Count > 0; round++)
        {
            List<PieceModel> going = new List<PieceModel>();
            foreach (PieceModel piece in look)
            {
                string? stop = Judge(piece, byCell, removed, devices, blocked, out bool goes);
                if (stop != null)
                {
                    stops[piece.Id] = stop;
                }
                else if (goes)
                {
                    going.Add(piece);
                }
            }

            foreach (PieceModel piece in going)
            {
                removed.Add(piece.Id);
                order.Add(new KeyValuePair<long, int>(piece.Id, round));
            }

            look = NextToLook(going, byCell, removed, stops, candidateIds);
        }

        return new PruneResult(order, stops);
    }

    // A stub or isolated piece goes unless something stops it; anything with two connected ends stays silently.
    private static string? Judge(PieceModel piece, Dictionary<GridCell, List<PieceModel>> byCell,
        HashSet<long> removed, HashSet<long> devices, IReadOnlyDictionary<long, string> blocked, out bool goes)
    {
        goes = false;
        if (removed.Contains(piece.Id))
        {
            return null;
        }

        List<PieceModel> neighbours = NeighboursOf(piece, byCell, removed);
        if (Connectivity.ConnectedEnds(piece, neighbours).Count > 1)
        {
            return null;
        }

        foreach (PieceModel neighbour in neighbours)
        {
            if (devices.Contains(neighbour.Id) &&
                (Connectivity.Links(piece, neighbour) || Connectivity.Links(neighbour, piece)))
            {
                return DeviceConnected;
            }
        }

        if (blocked.TryGetValue(piece.Id, out string? reason) && reason != null)
        {
            return reason;
        }

        goes = true;
        return null;
    }

    // The candidates next to what just went that are neither gone nor already stopped.
    private static List<PieceModel> NextToLook(List<PieceModel> going, Dictionary<GridCell, List<PieceModel>> byCell,
        HashSet<long> removed, Dictionary<long, string> stops, HashSet<long> candidates)
    {
        List<PieceModel> next = new List<PieceModel>();
        HashSet<long> seen = new HashSet<long>();
        foreach (PieceModel piece in going)
        {
            foreach (PieceModel neighbour in NeighboursOf(piece, byCell, removed))
            {
                if (candidates.Contains(neighbour.Id) && !stops.ContainsKey(neighbour.Id) && seen.Add(neighbour.Id))
                {
                    next.Add(neighbour);
                }
            }
        }

        return next;
    }

    // Everything in the cells the piece's ends sit in: only those can link to it either way (Connectivity).
    private static List<PieceModel> NeighboursOf(PieceModel piece, Dictionary<GridCell, List<PieceModel>> byCell,
        HashSet<long> removed)
    {
        List<PieceModel> neighbours = new List<PieceModel>();
        foreach (PieceEnd end in piece.Ends)
        {
            if (!byCell.TryGetValue(end.Local, out List<PieceModel>? there))
            {
                continue;
            }

            foreach (PieceModel other in there)
            {
                if (other.Id != piece.Id && !removed.Contains(other.Id) && !neighbours.Contains(other))
                {
                    neighbours.Add(other);
                }
            }
        }

        return neighbours;
    }

    private static Dictionary<GridCell, List<PieceModel>> IndexByCell(IReadOnlyList<PieceModel> candidates,
        IReadOnlyList<PieceModel> others)
    {
        Dictionary<GridCell, List<PieceModel>> byCell = new Dictionary<GridCell, List<PieceModel>>();
        AddAll(byCell, candidates);
        AddAll(byCell, others);
        return byCell;
    }

    private static void AddAll(Dictionary<GridCell, List<PieceModel>> byCell, IReadOnlyList<PieceModel> models)
    {
        foreach (PieceModel model in models)
        {
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
    }
}
