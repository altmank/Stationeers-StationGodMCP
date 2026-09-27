#nullable enable

using System.Collections.Generic;

namespace StationGodMCP.Pure;

/// <summary>Consecutive single straights to be replaced by one long straight, and that long straight's model.</summary>
internal sealed class StraightSegment
{
    internal StraightSegment(List<PieceModel> singles, PieceModel longPiece)
    {
        Singles = singles;
        Long = longPiece;
    }

    /// <summary>The singles, in order along the line.</summary>
    internal List<PieceModel> Singles { get; }

    /// <summary>The long straight covering exactly their cells, under the first single's id.</summary>
    internal PieceModel Long { get; }
}

/// <summary>
/// merge_straights on plain values: the inverse of LongStraights.Split. A run is a chain of single straights (one
/// cell, two opposite ends of one type and role) of one kind (the caller's key: grade, colour, owner), each linked
/// both ways to the next along one line. Nothing else can sit inside a run: a junction or a device breaks it, since a
/// single has no end across the line. Each run is covered from its start by the longest pieces the coil or kit offers
/// that still fit (greedy, e.g. 10, then 5, then 3); what is left over stays as singles. The long piece exposes only
/// the run's two tip ends, which are exactly the ends of the singles at its tips, so every link out stays.
/// </summary>
internal static class StraightRuns
{
    internal static List<StraightSegment> Plan(IReadOnlyList<PieceModel> singles, IReadOnlyDictionary<long, int> keys,
        IReadOnlyList<int> lengths)
    {
        List<StraightSegment> segments = new List<StraightSegment>();
        foreach (List<PieceModel> run in Runs(singles, keys))
        {
            foreach (KeyValuePair<int, int> part in Cover(run.Count, lengths))
            {
                List<PieceModel> covered = run.GetRange(part.Key, part.Value);
                segments.Add(new StraightSegment(covered, Join(covered)));
            }
        }

        return segments;
    }

    /// <summary>One cell, two ends of one type and role pointing opposite ways along one axis.</summary>
    internal static bool IsSingleStraight(PieceModel piece)
    {
        if (piece.Cells.Count != 1 || piece.Ends.Count != 2)
        {
            return false;
        }

        PieceEnd a = piece.Ends[0];
        PieceEnd b = piece.Ends[1];
        GridCell cell = piece.Cells[0];
        return a.Type == b.Type && a.Role == b.Role && a.Facing.Equals(cell) && b.Facing.Equals(cell) &&
               OneAxis(Step(cell, a.Local)) && Step(cell, a.Local).Equals(Negate(Step(cell, b.Local)));
    }

    /// <summary>
    /// Start index and length of each long piece covering a run of <paramref name="count"/> singles, longest first
    /// wherever it still fits; lengths shorter than 2 are ignored.
    /// </summary>
    internal static List<KeyValuePair<int, int>> Cover(int count, IReadOnlyList<int> lengths)
    {
        List<int> usable = new List<int>();
        foreach (int length in lengths)
        {
            if (length >= 2 && !usable.Contains(length))
            {
                usable.Add(length);
            }
        }

        usable.Sort(static (a, b) => b.CompareTo(a));
        List<KeyValuePair<int, int>> parts = new List<KeyValuePair<int, int>>();
        int at = 0;
        foreach (int length in usable)
        {
            while (count - at >= length)
            {
                parts.Add(new KeyValuePair<int, int>(at, length));
                at += length;
            }
        }

        return parts;
    }

    /// <summary>The long straight covering the singles (in line order): their cells, and the two tip ends.</summary>
    internal static PieceModel Join(IReadOnlyList<PieceModel> singles)
    {
        PieceModel first = singles[0];
        PieceModel last = singles[singles.Count - 1];
        List<GridCell> cells = new List<GridCell>(singles.Count);
        foreach (PieceModel single in singles)
        {
            cells.Add(single.Cells[0]);
        }

        PieceEnd head = OutwardEnd(first, singles.Count > 1 ? singles[1].Cells[0] : (GridCell?)null);
        PieceEnd tail = OutwardEnd(last, singles.Count > 1 ? singles[singles.Count - 2].Cells[0] : (GridCell?)null);
        return new PieceModel(first.Id, cells, new[] { head, tail }, first.Content);
    }

    /// <summary>Every run of two or more linked singles of one key, each in order along its line.</summary>
    internal static List<List<PieceModel>> Runs(IReadOnlyList<PieceModel> singles, IReadOnlyDictionary<long, int> keys)
    {
        Dictionary<long, List<PieceModel>> next = Adjacency(singles, keys);
        HashSet<long> placed = new HashSet<long>();
        List<List<PieceModel>> runs = new List<List<PieceModel>>();
        foreach (PieceModel start in singles)
        {
            if (placed.Contains(start.Id) || !next.TryGetValue(start.Id, out List<PieceModel>? links) ||
                links.Count != 1)
            {
                continue;
            }

            List<PieceModel> run = Walk(start, next, placed);
            if (run.Count >= 2)
            {
                runs.Add(run);
            }
        }

        return runs;
    }

    private static List<PieceModel> Walk(PieceModel start, Dictionary<long, List<PieceModel>> next,
        HashSet<long> placed)
    {
        List<PieceModel> run = new List<PieceModel> { start };
        placed.Add(start.Id);
        PieceModel at = start;
        while (true)
        {
            PieceModel? onward = null;
            foreach (PieceModel candidate in next[at.Id])
            {
                if (!placed.Contains(candidate.Id))
                {
                    onward = candidate;
                }
            }

            if (onward == null)
            {
                return run;
            }

            run.Add(onward);
            placed.Add(onward.Id);
            at = onward;
        }
    }

    // Singles linked both ways, of one key and one axis. A single has at most two such neighbours.
    private static Dictionary<long, List<PieceModel>> Adjacency(IReadOnlyList<PieceModel> singles,
        IReadOnlyDictionary<long, int> keys)
    {
        Dictionary<GridCell, PieceModel> byCell = new Dictionary<GridCell, PieceModel>();
        Dictionary<long, List<PieceModel>> next = new Dictionary<long, List<PieceModel>>();
        foreach (PieceModel single in singles)
        {
            if (IsSingleStraight(single))
            {
                byCell[single.Cells[0]] = single;
                next[single.Id] = new List<PieceModel>(2);
            }
        }

        foreach (PieceModel single in singles)
        {
            if (!next.ContainsKey(single.Id))
            {
                continue;
            }

            foreach (PieceEnd end in single.Ends)
            {
                if (byCell.TryGetValue(end.Local, out PieceModel? other) && SameKey(keys, single, other) &&
                    Connectivity.Links(single, other) && Connectivity.Links(other, single) &&
                    SameAxis(single, other))
                {
                    next[single.Id].Add(other);
                }
            }
        }

        return next;
    }

    private static bool SameKey(IReadOnlyDictionary<long, int> keys, PieceModel a, PieceModel b) =>
        keys.TryGetValue(a.Id, out int left) && keys.TryGetValue(b.Id, out int right) && left == right;

    private static bool SameAxis(PieceModel a, PieceModel b)
    {
        GridCell stepA = Step(a.Cells[0], a.Ends[0].Local);
        GridCell stepB = Step(b.Cells[0], b.Ends[0].Local);
        return stepA.Equals(stepB) || stepA.Equals(Negate(stepB));
    }

    // The single's end that does not point at its neighbour in the run.
    private static PieceEnd OutwardEnd(PieceModel single, GridCell? neighbour)
    {
        if (neighbour.HasValue && single.Ends[0].Local.Equals(neighbour.Value))
        {
            return single.Ends[1];
        }

        return single.Ends[0];
    }

    private static GridCell Step(GridCell from, GridCell to) =>
        new GridCell(to.X - from.X, to.Y - from.Y, to.Z - from.Z);

    private static GridCell Negate(GridCell step) => new GridCell(-step.X, -step.Y, -step.Z);

    private static bool OneAxis(GridCell step) =>
        (step.X != 0 ? 1 : 0) + (step.Y != 0 ? 1 : 0) + (step.Z != 0 ? 1 : 0) == 1;
}
