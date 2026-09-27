#nullable enable

using System;
using System.Collections.Generic;

namespace StationGodMCP.Pure;

/// <summary>
/// A lower bound on the extra cost a route still has to pay for air cells, added to the search's distance heuristic
/// so the search stays exact and fast under frames_first. Every route costs at least its length plus the penalty per
/// air cell it enters, so the distance plus the penalty times a lower bound on the air cells still to enter never
/// overestimates. Two ways to bound the air cells, by the size of the search box:
/// - Field (up to MaximumSmallCells small cells): the fewest air cells any path inside the box from the cell to a
///   goal enters, obstacles ignored: a 0-1 breadth-first search from the goals (entering an air cell costs 1, a
///   supported cell or a goal 0). Exact where nothing blocks, so the search heads straight for the best crossing
///   even when the supported cells around the two ends are not joined to each other.
/// - Gaps (larger boxes, up to MaximumLargeCells 2 m cells): a route either enters no supported cell before the goal
///   (at least D - 1 air cells at distance D) or reaches one after at least a steps and leaves the last one at least
///   b steps before the goal ((a - 1) + (b - 1)). a and b come from a distance transform on the 2 m grid: every
///   supported small cell lies in the closed box of an anchor (CellSupports.Anchors), and a small cell whose 2 m cell
///   is k steps (L1) from the nearest cell within one of an anchor is at least 4k small steps from that anchor's box.
/// Beyond both, and outside the box, the bound is zero: the search is still exact, only slower.
/// </summary>
internal sealed class AirBound
{
    /// <summary>The most small cells the field may cover.</summary>
    internal const int MaximumSmallCells = 524288;

    /// <summary>The most 2 m cells the gap transform may cover (the box plus one around it).</summary>
    internal const int MaximumLargeCells = 262144;

    private const int Unreached = int.MaxValue;

    private readonly double _penalty;
    private readonly BoxValues? _field;
    private readonly BoxValues? _gaps;
    private readonly int _goalGap;

    private AirBound(double penalty, BoxValues? field, BoxValues? gaps, IReadOnlyList<GridCell> goals)
    {
        _penalty = penalty;
        _field = field;
        _gaps = gaps;
        _goalGap = int.MaxValue;
        if (gaps != null)
        {
            foreach (GridCell goal in goals)
            {
                _goalGap = Math.Min(_goalGap, Gap(goal));
            }
        }
    }

    /// <summary>Which way the bound was made: field, gaps or none.</summary>
    internal string Kind => _field != null ? "field" : _gaps != null ? "gaps" : "none";

    /// <summary>
    /// The bound for a search box (small-cell corners, both included) and its goals. support classifies small cells
    /// (asked once for each cell of the box when the field fits); anchored says whether a 2 m cell is an anchor
    /// (asked once for each 2 m cell of the box and one more around it when only the gaps fit). fieldLimit lowers
    /// MaximumSmallCells (tests reach the gaps with it).
    /// </summary>
    internal static AirBound Build(GridCell min, GridCell max, IReadOnlyList<GridCell> goals,
        Func<GridCell, CellSupport> support, Func<GridCell, bool> anchored, double penalty,
        int fieldLimit = MaximumSmallCells)
    {
        BoxShape small = BoxShape.Between(min, max, GridStep.CellSize);
        if (small.Count <= Math.Min(fieldLimit, MaximumSmallCells))
        {
            return new AirBound(penalty, Field(small, goals, support), null, goals);
        }

        int step = SmallCellCode.Large;
        GridCell low = SmallCellCode.LargeOf(min);
        GridCell high = SmallCellCode.LargeOf(max);
        BoxShape large = BoxShape.Between(new GridCell(low.X - step, low.Y - step, low.Z - step),
            new GridCell(high.X + step, high.Y + step, high.Z + step), step);
        return large.Count <= MaximumLargeCells
            ? new AirBound(penalty, null, Gaps(large, anchored), goals)
            : new AirBound(penalty, null, null, goals);
    }

    /// <summary>The penalty times the fewest air cells any route from the cell to a goal still enters.</summary>
    internal double Extra(GridCell cell, double cellsToGoal)
    {
        double air = 0.0;
        if (_field != null)
        {
            int index = _field.Shape.IndexOf(cell);
            int value = index >= 0 ? _field.Values[index] : Unreached;
            air = value == Unreached ? 0.0 : value;
        }
        else if (_gaps != null)
        {
            double throughSupport = (double)Math.Max(0, Gap(cell) - 1) + Math.Max(0, _goalGap - 1);
            air = Math.Min(cellsToGoal - 1.0, throughSupport);
        }

        return air > 0.0 ? air * _penalty : 0.0;
    }

    // The fewest air cells entered from each small cell of the box to a goal: a 0-1 breadth-first search from the
    // goals, one queue per level.
    private static BoxValues Field(BoxShape shape, IReadOnlyList<GridCell> goals, Func<GridCell, CellSupport> support)
    {
        BoxValues field = new BoxValues(shape, Unreached);
        int[] values = field.Values;
        bool[] air = new bool[shape.Count];
        for (int index = 0; index < shape.Count; index++)
        {
            air[index] = support(shape.CellAt(index)) == CellSupport.Air;
        }

        Queue<int> level = new Queue<int>();
        Queue<int> nextLevel = new Queue<int>();
        foreach (GridCell goal in goals)
        {
            int index = shape.IndexOf(goal);
            if (index >= 0)
            {
                // Entering a goal is free.
                air[index] = false;
                Seed(index);
                continue;
            }

            // A goal outside the box (a branch's tree reaches past it) is entered from a cell of the box beside it.
            foreach (GridStep step in GridStep.All)
            {
                int beside = shape.IndexOf(step.From(goal));
                if (beside >= 0)
                {
                    Seed(beside);
                }
            }
        }

        int[] around = new int[6];
        int depth = 0;
        while (level.Count > 0 || nextLevel.Count > 0)
        {
            if (level.Count == 0)
            {
                (level, nextLevel) = (nextLevel, level);
                depth++;
            }

            int cell = level.Dequeue();
            if (values[cell] != depth)
            {
                continue;
            }

            // A path from a neighbour enters this cell, which costs 1 when it is air.
            int cost = air[cell] ? 1 : 0;
            int reached = depth + cost;
            int count = shape.Neighbours(cell, around);
            for (int n = 0; n < count; n++)
            {
                int neighbour = around[n];
                if (values[neighbour] <= reached)
                {
                    continue;
                }

                values[neighbour] = reached;
                (cost == 0 ? level : nextLevel).Enqueue(neighbour);
            }
        }

        return field;

        void Seed(int index)
        {
            if (values[index] != 0)
            {
                values[index] = 0;
                level.Enqueue(index);
            }
        }
    }

    // A lower bound on the small steps from the cell to any supported cell: 0 when unknown, and a quarter of
    // int.MaxValue when none is in the box (so two of them still add up without overflow).
    private int Gap(GridCell small)
    {
        int index = _gaps!.Shape.IndexOf(SmallCellCode.LargeOf(small));
        if (index < 0)
        {
            return 0;
        }

        int steps = _gaps.Values[index];
        return steps == Unreached ? int.MaxValue / 4 : steps * SmallCellCode.PerAxis;
    }

    // L1 distance, on the 2 m grid, to the nearest cell within one (along every axis) of an anchor.
    private static BoxValues Gaps(BoxShape shape, Func<GridCell, bool> anchored)
    {
        BoxValues gaps = new BoxValues(shape, Unreached);
        int[] values = gaps.Values;
        Queue<int> queue = new Queue<int>();
        List<int> near = new List<int>(27);
        for (int index = 0; index < shape.Count; index++)
        {
            if (!anchored(shape.CellAt(index)))
            {
                continue;
            }

            shape.Around(index, near);
            foreach (int cell in near)
            {
                if (values[cell] != 0)
                {
                    values[cell] = 0;
                    queue.Enqueue(cell);
                }
            }
        }

        int[] around = new int[6];
        while (queue.Count > 0)
        {
            int cell = queue.Dequeue();
            int next = values[cell] + 1;
            int count = shape.Neighbours(cell, around);
            for (int n = 0; n < count; n++)
            {
                if (values[around[n]] > next)
                {
                    values[around[n]] = next;
                    queue.Enqueue(around[n]);
                }
            }
        }

        return gaps;
    }

    /// <summary>A box of cells spaced step apart, both corners included, indexed x fastest.</summary>
    private sealed class BoxShape
    {
        private readonly GridCell _origin;
        private readonly int _sizeX;
        private readonly int _sizeY;
        private readonly int _sizeZ;
        private readonly int _step;

        private BoxShape(GridCell origin, int sizeX, int sizeY, int sizeZ, int step)
        {
            _origin = origin;
            _sizeX = sizeX;
            _sizeY = sizeY;
            _sizeZ = sizeZ;
            _step = step;
            long count = (long)sizeX * sizeY * sizeZ;
            Count = count > int.MaxValue ? int.MaxValue : (int)count;
        }

        internal static BoxShape Between(GridCell min, GridCell max, int step) =>
            new BoxShape(min, (max.X - min.X) / step + 1, (max.Y - min.Y) / step + 1, (max.Z - min.Z) / step + 1,
                step);

        internal int Count { get; }

        internal int IndexOf(GridCell cell)
        {
            int dx = cell.X - _origin.X;
            int dy = cell.Y - _origin.Y;
            int dz = cell.Z - _origin.Z;
            if (dx < 0 || dy < 0 || dz < 0 || dx % _step != 0 || dy % _step != 0 || dz % _step != 0)
            {
                return -1;
            }

            int x = dx / _step;
            int y = dy / _step;
            int z = dz / _step;
            return x < _sizeX && y < _sizeY && z < _sizeZ ? x + _sizeX * (y + _sizeY * z) : -1;
        }

        internal GridCell CellAt(int index) =>
            new GridCell(_origin.X + index % _sizeX * _step, _origin.Y + index / _sizeX % _sizeY * _step,
                _origin.Z + index / (_sizeX * _sizeY) * _step);

        /// <summary>Writes the up to six face neighbours inside the box into the buffer; returns how many.</summary>
        internal int Neighbours(int index, int[] into)
        {
            int x = index % _sizeX;
            int y = index / _sizeX % _sizeY;
            int z = index / (_sizeX * _sizeY);
            int plane = _sizeX * _sizeY;
            int count = 0;
            if (x > 0) into[count++] = index - 1;
            if (x < _sizeX - 1) into[count++] = index + 1;
            if (y > 0) into[count++] = index - _sizeX;
            if (y < _sizeY - 1) into[count++] = index + _sizeX;
            if (z > 0) into[count++] = index - plane;
            if (z < _sizeZ - 1) into[count++] = index + plane;
            return count;
        }

        /// <summary>The cell and every cell within one of it along every axis, inside the box.</summary>
        internal void Around(int index, List<int> into)
        {
            into.Clear();
            int x = index % _sizeX;
            int y = index / _sizeX % _sizeY;
            int z = index / (_sizeX * _sizeY);
            for (int cz = Math.Max(0, z - 1); cz <= Math.Min(_sizeZ - 1, z + 1); cz++)
            {
                for (int cy = Math.Max(0, y - 1); cy <= Math.Min(_sizeY - 1, y + 1); cy++)
                {
                    for (int cx = Math.Max(0, x - 1); cx <= Math.Min(_sizeX - 1, x + 1); cx++)
                    {
                        into.Add(cx + _sizeX * (cy + _sizeY * cz));
                    }
                }
            }
        }
    }

    /// <summary>One int per cell of a box.</summary>
    private sealed class BoxValues
    {
        internal BoxValues(BoxShape shape, int initial)
        {
            Shape = shape;
            Values = new int[shape.Count];
            for (int index = 0; index < Values.Length; index++)
            {
                Values[index] = initial;
            }
        }

        internal BoxShape Shape { get; }

        internal int[] Values { get; }
    }
}
