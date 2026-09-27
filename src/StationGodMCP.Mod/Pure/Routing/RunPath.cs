#nullable enable

using System.Collections.Generic;

namespace StationGodMCP.Pure;

/// <summary>
/// A run as the cells it passes through, in order, from the caller's form: every cell listed (each a neighbour of
/// the one before), or waypoints joined by straight axis-aligned lines (the caller decides the route; nothing is
/// searched here). A run never visits a cell twice: a run crossing itself would need a junction nobody asked for.
/// </summary>
internal static class RunPath
{
    internal const int MaximumCells = 1024;

    /// <summary>The cells between each waypoint and the next, both included; null with the reason when invalid.</summary>
    internal static List<GridCell>? FromWaypoints(IReadOnlyList<GridCell> points, out string? error)
    {
        if (points.Count == 0)
        {
            error = "A run needs at least one point.";
            return null;
        }

        List<GridCell> cells = new List<GridCell> { points[0] };
        if (!OnGrid(points, out error))
        {
            return null;
        }

        for (int index = 1; index < points.Count; index++)
        {
            GridCell from = points[index - 1];
            GridCell to = points[index];
            if (from.Equals(to))
            {
                continue;
            }

            GridStep? step = Direction(from, to);
            if (!step.HasValue)
            {
                error = $"Waypoints {index - 1} {from} and {index} {to} are not on one axis line; the run goes in " +
                        "straight axis-aligned lines only, so add a waypoint at the corner.";
                return null;
            }

            int length = Distance(from, to) / GridStep.CellSize;
            for (int n = 1; n <= length; n++)
            {
                cells.Add(step.Value.From(from, n));
                if (cells.Count > MaximumCells)
                {
                    error = $"The run is longer than {MaximumCells} cells.";
                    return null;
                }
            }
        }

        return Distinct(cells, out error) ? cells : null;
    }

    /// <summary>The cells as listed, each a neighbour of the one before; null with the reason when invalid.</summary>
    internal static List<GridCell>? FromCells(IReadOnlyList<GridCell> listed, out string? error)
    {
        if (listed.Count == 0)
        {
            error = "A run needs at least one cell.";
            return null;
        }

        if (listed.Count > MaximumCells)
        {
            error = $"The run is longer than {MaximumCells} cells.";
            return null;
        }

        if (!OnGrid(listed, out error))
        {
            return null;
        }

        List<GridCell> cells = new List<GridCell>(listed.Count);
        for (int index = 0; index < listed.Count; index++)
        {
            if (index > 0 && !GridStep.Between(listed[index - 1], listed[index]).HasValue)
            {
                error = $"Cells {index - 1} {listed[index - 1]} and {index} {listed[index]} are not neighbours " +
                        "(0.5 m apart along one axis); use waypoints for longer straight lines.";
                return null;
            }

            cells.Add(listed[index]);
        }

        return Distinct(cells, out error) ? cells : null;
    }

    /// <summary>Each cell's directions to the cells before and after it in the run.</summary>
    internal static Dictionary<GridCell, EndSet> Ends(IReadOnlyList<GridCell> cells)
    {
        Dictionary<GridCell, EndSet> ends = new Dictionary<GridCell, EndSet>(cells.Count);
        for (int index = 0; index < cells.Count; index++)
        {
            EndSet set = EndSet.None;
            if (index > 0)
            {
                set = set.With(GridStep.Between(cells[index], cells[index - 1])!.Value);
            }

            if (index < cells.Count - 1)
            {
                set = set.With(GridStep.Between(cells[index], cells[index + 1])!.Value);
            }

            ends[cells[index]] = set;
        }

        return ends;
    }

    /// <summary>How many times the run turns.</summary>
    internal static int Bends(IReadOnlyList<GridCell> cells)
    {
        int bends = 0;
        for (int index = 1; index < cells.Count - 1; index++)
        {
            GridStep? a = GridStep.Between(cells[index - 1], cells[index]);
            GridStep? b = GridStep.Between(cells[index], cells[index + 1]);
            if (a.HasValue && b.HasValue && !a.Value.Equals(b.Value))
            {
                bends++;
            }
        }

        return bends;
    }

    /// <summary>The run as waypoints: its first cell, every cell it turns in, and its last cell.</summary>
    internal static List<GridCell> Waypoints(IReadOnlyList<GridCell> cells)
    {
        List<GridCell> points = new List<GridCell>();
        for (int index = 0; index < cells.Count; index++)
        {
            bool turn = index > 0 && index < cells.Count - 1 &&
                        !GridStep.Between(cells[index - 1], cells[index])!.Value
                            .Equals(GridStep.Between(cells[index], cells[index + 1])!.Value);
            if (index == 0 || index == cells.Count - 1 || turn)
            {
                points.Add(cells[index]);
            }
        }

        return points;
    }

    private static bool OnGrid(IReadOnlyList<GridCell> cells, out string? error)
    {
        for (int index = 0; index < cells.Count; index++)
        {
            if (!GridStep.IsOnGrid(cells[index]))
            {
                error = $"Point {index} {cells[index]} is not a small-grid cell centre.";
                return false;
            }
        }

        error = null;
        return true;
    }

    private static bool Distinct(List<GridCell> cells, out string? error)
    {
        HashSet<GridCell> seen = new HashSet<GridCell>();
        for (int index = 0; index < cells.Count; index++)
        {
            if (!seen.Add(cells[index]))
            {
                error = $"The run visits cell {cells[index]} twice (at step {index}); a run may not cross or " +
                        "retrace itself.";
                return false;
            }
        }

        error = null;
        return true;
    }

    private static GridStep? Direction(GridCell from, GridCell to)
    {
        int x = to.X - from.X;
        int y = to.Y - from.Y;
        int z = to.Z - from.Z;
        int moved = (x != 0 ? 1 : 0) + (y != 0 ? 1 : 0) + (z != 0 ? 1 : 0);
        if (moved != 1)
        {
            return null;
        }

        int axis = x != 0 ? 0 : y != 0 ? 1 : 2;
        return GridStep.All[axis * 2 + (x + y + z > 0 ? 0 : 1)];
    }

    private static int Distance(GridCell a, GridCell b) =>
        System.Math.Abs(a.X - b.X) + System.Math.Abs(a.Y - b.Y) + System.Math.Abs(a.Z - b.Z);
}

/// <summary>A free end of a run: its cell, and the run cell behind it (null for a one-cell run).</summary>
internal readonly struct RunTip
{
    internal RunTip(GridCell cell, GridCell? behind)
    {
        Cell = cell;
        Behind = behind;
    }

    internal GridCell Cell { get; }

    internal GridCell? Behind { get; }
}

/// <summary>A branch of a run: its cells in order, and the cell it attaches to (null: the first neighbour found).</summary>
internal sealed class RunBranch
{
    internal RunBranch(IReadOnlyList<GridCell> cells, GridCell? attach)
    {
        Cells = cells;
        Attach = attach;
    }

    internal IReadOnlyList<GridCell> Cells { get; }

    internal GridCell? Attach { get; }
}

/// <summary>
/// A run and its branches as one tree of cells. The main run goes from its first cell to its last; each branch is a
/// path whose last cell is a neighbour of a cell laid before it, where it attaches (the cell named, else the main
/// run's, then earlier branches', first such in that order): the two cells get ends towards each other. Cells: the
/// main run's, then each branch's, none twice. Tips: the main run's first and last cells and each branch's first
/// cell, which join what is around them as a run end does. Legs: the direction items travel (chutes): along the main
/// run, and along each branch into the cell it attaches to. Fills: cells a split long straight leaves, each with the
/// ends it had, laid as new pieces but joining nothing else (they are not tips).
/// </summary>
internal sealed class RunShape
{
    private readonly HashSet<GridCell> _fills;

    private RunShape(List<GridCell> cells, Dictionary<GridCell, EndSet> ends, List<RunTip> tips, List<RunLeg> legs,
        List<GridCell> main, HashSet<GridCell> fills)
    {
        Cells = cells;
        Ends = ends;
        Tips = tips;
        Legs = legs;
        Main = main;
        _fills = fills;
    }

    internal List<GridCell> Cells { get; }

    internal Dictionary<GridCell, EndSet> Ends { get; }

    internal List<RunTip> Tips { get; }

    internal List<RunLeg> Legs { get; }

    /// <summary>The main run's cells in order.</summary>
    internal List<GridCell> Main { get; }

    internal bool IsTip(GridCell cell) => Tips.Exists(tip => tip.Cell.Equals(cell));

    /// <summary>A cell only a split long straight put in the shape (not one the caller's run passes).</summary>
    internal bool IsFill(GridCell cell) => _fills.Contains(cell);

    /// <summary>A plain run with no branches.</summary>
    internal static RunShape Line(IReadOnlyList<GridCell> run) =>
        Of(run, new List<RunBranch>(), out string? _) ?? throw new System.ArgumentException("empty run");

    /// <summary>The tree; null with the reason when a branch reuses a cell or attaches to nothing.</summary>
    internal static RunShape? Of(IReadOnlyList<GridCell> run, IReadOnlyList<RunBranch> branches, out string? error)
    {
        List<GridCell> cells = new List<GridCell>(run);
        HashSet<GridCell> laid = new HashSet<GridCell>(run);
        Dictionary<GridCell, EndSet> ends = RunPath.Ends(run);
        List<RunTip> tips = new List<RunTip>();
        List<RunLeg> legs = RunLeg.Of(run);
        if (run.Count > 0)
        {
            tips.Add(new RunTip(run[0], run.Count > 1 ? run[1] : (GridCell?)null));
            if (run.Count > 1)
            {
                tips.Add(new RunTip(run[run.Count - 1], run[run.Count - 2]));
            }
        }

        for (int index = 0; index < branches.Count; index++)
        {
            IReadOnlyList<GridCell> branch = branches[index].Cells;
            if (branch.Count == 0)
            {
                error = $"Branch {index} has no cells.";
                return null;
            }

            foreach (GridCell cell in branch)
            {
                if (laid.Contains(cell))
                {
                    error = $"Branch {index} passes through {cell}, which the run or an earlier branch already takes.";
                    return null;
                }
            }

            if (cells.Count + branch.Count > RunPath.MaximumCells)
            {
                error = $"The run and its branches are longer than {RunPath.MaximumCells} cells.";
                return null;
            }

            GridCell last = branch[branch.Count - 1];
            GridCell? named = branches[index].Attach;
            int at = named.HasValue
                ? cells.FindIndex(cell => cell.Equals(named.Value) && GridStep.Between(last, cell).HasValue)
                : cells.FindIndex(cell => GridStep.Between(last, cell).HasValue);
            if (at < 0)
            {
                error = named.HasValue
                    ? $"Branch {index} ends at {last}, which is not next to {named.Value} on the run or an earlier " +
                      "branch."
                    : $"Branch {index} ends at {last}, which is next to no cell of the run or an earlier branch.";
                return null;
            }

            GridCell attach = cells[at];
            foreach (KeyValuePair<GridCell, EndSet> entry in RunPath.Ends(branch))
            {
                ends[entry.Key] = entry.Value;
            }

            GridStep toward = GridStep.Between(last, attach)!.Value;
            ends[last] = ends[last].With(toward);
            ends[attach] = ends[attach].With(toward.Opposite);
            cells.AddRange(branch);
            laid.UnionWith(branch);
            tips.Add(new RunTip(branch[0], branch.Count > 1 ? branch[1] : attach));
            legs.AddRange(RunLeg.Of(branch));
            legs.Add(new RunLeg(last, toward));
        }

        error = null;
        return new RunShape(cells, ends, tips, legs, new List<GridCell>(run), new HashSet<GridCell>());
    }

    /// <summary>
    /// The shape with cells a split long straight leaves: each gets the ends it had there, added to any the run
    /// already gives it.
    /// </summary>
    internal RunShape WithFills(IReadOnlyDictionary<GridCell, EndSet> fills)
    {
        List<GridCell> cells = new List<GridCell>(Cells);
        Dictionary<GridCell, EndSet> ends = new Dictionary<GridCell, EndSet>(Ends);
        HashSet<GridCell> added = new HashSet<GridCell>(_fills);
        foreach (KeyValuePair<GridCell, EndSet> fill in fills)
        {
            if (ends.TryGetValue(fill.Key, out EndSet own))
            {
                ends[fill.Key] = own.Union(fill.Value);
                continue;
            }

            cells.Add(fill.Key);
            ends[fill.Key] = fill.Value;
            added.Add(fill.Key);
        }

        return new RunShape(cells, ends, Tips, Legs, Main, added);
    }
}
