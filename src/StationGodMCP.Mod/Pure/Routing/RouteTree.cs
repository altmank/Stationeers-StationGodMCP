#nullable enable

using System;
using System.Collections.Generic;

namespace StationGodMCP.Pure;

/// <summary>
/// A route end as resolved: the cells the search may start or end at (one for a start; a long straight's cells or a
/// whole network's for a target), the kind's networks it is on, and for a device port the direction from its cell
/// into the device.
/// </summary>
internal sealed class RouteEndpoint
{
    internal RouteEndpoint(List<RouteEnd> ends, List<long> networks, GridStep? intoDevice = null)
    {
        Ends = ends;
        Networks = networks;
        IntoDevice = intoDevice;
    }

    internal RouteEndpoint(RouteEnd end, List<long> networks, GridStep? intoDevice = null)
        : this(new List<RouteEnd> { end }, networks, intoDevice)
    {
    }

    internal List<RouteEnd> Ends { get; }

    /// <summary>The first (for a start, the only) cell.</summary>
    internal RouteEnd End => Ends[0];

    internal List<long> Networks { get; }

    /// <summary>A device port's end: the direction a piece in its cell needs to join it.</summary>
    internal GridStep? IntoDevice { get; }

    /// <summary>The end cell nearest the cell (Manhattan).</summary>
    internal RouteEnd NearestTo(GridCell cell)
    {
        RouteEnd best = Ends[0];
        int bestDistance = int.MaxValue;
        foreach (RouteEnd end in Ends)
        {
            int distance = Math.Abs(end.Cell.X - cell.X) + Math.Abs(end.Cell.Y - cell.Y) +
                           Math.Abs(end.Cell.Z - cell.Z);
            if (distance < bestDistance)
            {
                best = end;
                bestDistance = distance;
            }
        }

        return best;
    }
}

/// <summary>
/// Where a route tree's main run comes from: searched from the first start to a target, or a trunk laid as given
/// (bus mode: every start becomes a branch of it, so a trunk and its drops are one job).
/// </summary>
internal abstract class RouteMain
{
    private RouteMain()
    {
    }

    internal sealed class ToTarget : RouteMain
    {
        internal ToTarget(RouteEndpoint target)
        {
            Target = target;
        }

        internal RouteEndpoint Target { get; }
    }

    internal sealed class Trunk : RouteMain
    {
        internal Trunk(IReadOnlyList<GridCell> cells)
        {
            Cells = cells;
        }

        /// <summary>The trunk's cells in order, each a neighbour of the one before.</summary>
        internal IReadOnlyList<GridCell> Cells { get; }
    }
}

/// <summary>
/// What one leg of the tree searches with: the cost of a cell under the rules, the search box between a start and
/// the goal nearest it, and the frames_first air bound for a box and its goals (null without frames_first).
/// </summary>
internal sealed class RouteSearch
{
    internal RouteSearch(Func<GridCell, CellCost> cost, Func<GridCell, GridCell, RouteRules> box,
        Func<RouteRules, IReadOnlyList<RouteEnd>, AirBound?> air)
    {
        Cost = cost;
        Box = box;
        Air = air;
    }

    internal Func<GridCell, CellCost> Cost { get; }

    internal Func<GridCell, GridCell, RouteRules> Box { get; }

    internal Func<RouteRules, IReadOnlyList<RouteEnd>, AirBound?> Air { get; }
}

/// <summary>
/// Grows a route tree: the main run (searched from the first start to the target, or the trunk as given), then each
/// other start to the nearest cell of the tree so far (a branch, joined by a junction), never to the target again, so
/// no loop is made. A start the tree already passes through gets an extra end instead of a branch. A branch never
/// lands on a tree cell that already has three ends.
/// </summary>
internal static class RouteTrees
{
    /// <summary>The most starts one tree grows from (a generator network's ports, a room's drops).</summary>
    internal const int MaximumStarts = 16;

    /// <summary>The ends a tree piece may have before a branch may no longer attach to it.</summary>
    private const int MaximumAttachEnds = 3;

    internal static RouteTree Grow(IReadOnlyList<RouteEndpoint> starts, RouteMain main, RouteSearch search,
        double junctionCost)
    {
        RouteTree tree;
        int first;
        switch (main)
        {
            case RouteMain.Trunk trunk:
                tree = new RouteTree(new List<GridCell>(trunk.Cells), 0.0, 0);
                first = 0;
                break;
            case RouteMain.ToTarget target:
                RouteEndpoint origin = starts[0];
                RouteRules box = search.Box(origin.End.Cell, target.Target.NearestTo(origin.End.Cell).Cell);
                RouteResult found = RoutePlanner.FindAny(origin.End, target.Target.Ends, search.Cost, box,
                    search.Air(box, target.Target.Ends));
                if (found.Cells == null)
                {
                    return RouteTree.Failed(starts.Count > 1 ? "from[0]" : null, found);
                }

                tree = new RouteTree(found.Cells, found.Cost, found.Expanded);
                first = 1;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(main));
        }

        for (int index = first; index < starts.Count; index++)
        {
            RouteTree? failed = AddStart(tree, starts[index], $"from[{index}]", search, junctionCost);
            if (failed != null)
            {
                return failed;
            }
        }

        return tree;
    }

    // One start joined to the tree: an extra end where the tree already passes its cell, else a branch; null when
    // joined, the failed tree when no branch was found.
    private static RouteTree? AddStart(RouteTree tree, RouteEndpoint start, string which, RouteSearch search,
        double junctionCost)
    {
        if (tree.Contains(start.End.Cell))
        {
            if (start.IntoDevice.HasValue)
            {
                tree.AddExtra(new ExtraEnd(start.End.Cell, start.IntoDevice.Value));
            }

            return null;
        }

        List<RouteEnd> goals = tree.Cells.FindAll(cell => tree.EndsAt(cell) < MaximumAttachEnds)
            .ConvertAll(cell => new RouteEnd(cell, EndSet.None, junctionCost));
        RouteEndpoint treeEnds = new RouteEndpoint(goals, new List<long>());
        RouteRules box = search.Box(start.End.Cell, treeEnds.NearestTo(start.End.Cell).Cell);
        RouteResult branch = RoutePlanner.FindAny(start.End, goals,
            cell => tree.Contains(cell) ? CellCost.Blocked : search.Cost(cell), box, search.Air(box, goals));
        if (branch.Cells == null)
        {
            return RouteTree.Failed(which, branch);
        }

        tree.Add(branch);
        return null;
    }
}

/// <summary>The route found so far: the main run, its branches, extra ends for ports it passes, or a failure.</summary>
internal sealed class RouteTree
{
    private readonly HashSet<GridCell> _cells = new HashSet<GridCell>();
    private readonly Dictionary<GridCell, int> _ends = new Dictionary<GridCell, int>();

    internal RouteTree(List<GridCell> main, double cost, int expanded)
    {
        Main = main;
        Cost = cost;
        Expanded = expanded;
        Cells.AddRange(main);
        _cells.UnionWith(main);
        CountEnds(main);
    }

    private RouteTree(string? failedAt, RouteResult result)
    {
        Main = new List<GridCell>();
        FailedAt = failedAt;
        Failure = result.Failure ?? "no_route";
        Expanded = result.Expanded;
    }

    internal List<GridCell> Main { get; }

    internal List<RunBranch> Branches { get; } = new List<RunBranch>();

    internal List<ExtraEnd> Extra { get; } = new List<ExtraEnd>();

    /// <summary>Every cell of the tree: the main run's, then each branch's.</summary>
    internal List<GridCell> Cells { get; } = new List<GridCell>();

    internal double Cost { get; private set; }

    internal int Expanded { get; private set; }

    /// <summary>no_route, too_long or search_limit; null when the tree was grown.</summary>
    internal string? Failure { get; }

    /// <summary>Which start failed (from[i]) when there were several; null for the only one.</summary>
    internal string? FailedAt { get; }

    internal bool Found => Failure == null;

    /// <summary>The search gave up (search_limit) rather than finding no route.</summary>
    internal bool GaveUp => Failure == "search_limit";

    internal static RouteTree Failed(string? failedAt, RouteResult result) => new RouteTree(failedAt, result);

    internal bool Contains(GridCell cell) => _cells.Contains(cell);

    /// <summary>How many ends the piece in the cell has so far (a branch never lands on a junction already).</summary>
    internal int EndsAt(GridCell cell) => _ends.TryGetValue(cell, out int count) ? count : 0;

    internal void AddExtra(ExtraEnd end)
    {
        Extra.Add(end);
        Count(end.Cell);
    }

    // A branch search ends on a tree cell: the cells before it are the branch, attached to that cell.
    internal void Add(RouteResult branch)
    {
        List<GridCell> path = branch.Cells!;
        List<GridCell> cells = path.GetRange(0, path.Count - 1);
        Branches.Add(new RunBranch(cells, path[path.Count - 1]));
        Cells.AddRange(cells);
        _cells.UnionWith(cells);
        CountEnds(path);
        Cost += branch.Cost;
        Expanded += branch.Expanded;
    }

    // Each step of a path gives an end to both of its cells.
    private void CountEnds(List<GridCell> path)
    {
        for (int index = 1; index < path.Count; index++)
        {
            Count(path[index - 1]);
            Count(path[index]);
        }
    }

    private void Count(GridCell cell) => _ends[cell] = EndsAt(cell) + 1;
}
