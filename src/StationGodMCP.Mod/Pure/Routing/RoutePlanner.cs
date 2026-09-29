#nullable enable

using System;
using System.Collections.Generic;

namespace StationGodMCP.Pure;

/// <summary>Which axis a route should use first, when it has a choice.</summary>
internal enum AxisOrder
{
    Any,

    /// <summary>A vertical step after a horizontal one costs OrderPenalty: climb or drop first, then go across.</summary>
    VerticalFirst,

    /// <summary>A horizontal step after a vertical one costs OrderPenalty.</summary>
    HorizontalFirst
}

/// <summary>What one cell costs a route: impassable, or a cost of at least 1 per cell entered.</summary>
internal readonly struct CellCost
{
    private CellCost(bool passable, double cost, int blockedAxes)
    {
        Passable = passable;
        Cost = cost;
        BlockedAxes = blockedAxes;
    }

    internal static CellCost Blocked => new CellCost(false, double.PositiveInfinity, 7);

    internal bool Passable { get; }

    /// <summary>At least 1: preferences are extra cost on the cells not preferred, so the search stays exact.</summary>
    internal double Cost { get; }

    /// <summary>Axes the route may not run along in this cell (bit 0 x, 1 y, 2 z).</summary>
    internal int BlockedAxes { get; }

    internal static CellCost Of(double cost, int blockedAxes = 0) =>
        new CellCost(true, Math.Max(1.0, cost), blockedAxes & 7);

    internal bool Allows(GridStep step) => (BlockedAxes & (1 << step.Axis)) == 0;
}

/// <summary>The search's own rules: bends, axis order, length and search bounds.</summary>
internal sealed class RouteRules
{
    internal RouteRules(double bendCost, AxisOrder order, int maxLength, GridCell min, GridCell max)
    {
        BendCost = bendCost;
        Order = order;
        MaxLength = maxLength;
        Min = min;
        Max = max;
    }

    /// <summary>Extra cost of each turn (min_bends makes it large).</summary>
    internal double BendCost { get; }

    internal AxisOrder Order { get; }

    internal double OrderPenalty => 4.0;

    /// <summary>The most cells the route may have.</summary>
    internal int MaxLength { get; }

    /// <summary>The box the search stays in, both corners included.</summary>
    internal GridCell Min { get; }

    internal GridCell Max { get; }

    /// <summary>How many cells the search may expand before it gives up.</summary>
    internal int MaxExpanded => 200000;

    internal bool Inside(GridCell cell) =>
        cell.X >= Min.X && cell.X <= Max.X && cell.Y >= Min.Y && cell.Y <= Max.Y && cell.Z >= Min.Z &&
        cell.Z <= Max.Z;
}

/// <summary>
/// Where a route starts or ends: a cell, with the directions it may leave or arrive by for free (every direction for
/// an empty cell or a device port, an existing piece's open ends); any other direction costs ChangeCost (the piece
/// there becomes a junction).
/// </summary>
internal sealed class RouteEnd
{
    internal RouteEnd(GridCell cell, EndSet free, double changeCost)
    {
        Cell = cell;
        Free = free;
        ChangeCost = changeCost;
    }

    internal GridCell Cell { get; }

    internal EndSet Free { get; }

    internal double ChangeCost { get; }

    /// <summary>
    /// The extra cost of the route touching this end through the direction (seen from the end's cell); infinite when
    /// the end may not be changed (Leaving).
    /// </summary>
    internal double CostOf(GridStep step) => Free.Contains(step) ? 0.0 : ChangeCost;

    /// <summary>An empty cell: every direction is free.</summary>
    internal static RouteEnd Open(GridCell cell) => new RouteEnd(cell, EndSet.FromMask(0x3F), 0.0);

    /// <summary>A start the route may leave only through the free directions: a chute piece, which a junction cannot
    /// feed from (items only merge into a line there).</summary>
    internal static RouteEnd Leaving(GridCell cell, EndSet free) => new RouteEnd(cell, free, double.PositiveInfinity);

    /// <summary>
    /// The directions of the piece's ends at the cell that nothing is connected to. With leavingOnly (a chute start),
    /// only those items can leave through: not an Input end (ChuteRoles.TakesIn), which takes items into the piece.
    /// </summary>
    internal static EndSet OpenAt(PieceModel piece, GridCell cell, IReadOnlyList<PieceEnd> connected,
        bool leavingOnly)
    {
        EndSet open = EndSet.None;
        foreach (PieceEnd end in piece.Ends)
        {
            GridStep? step = end.Facing.Equals(cell) ? GridStep.Between(cell, end.Local) : null;
            if (!step.HasValue || (leavingOnly && ChuteRoles.TakesIn(end.Role)))
            {
                continue;
            }

            bool joined = false;
            foreach (PieceEnd other in connected)
            {
                joined |= other.Local.Equals(end.Local);
            }

            if (!joined)
            {
                open = open.With(step.Value);
            }
        }

        return open;
    }
}

/// <summary>A route found, or why none was.</summary>
internal sealed class RouteResult
{
    private RouteResult(List<GridCell>? cells, double cost, int expanded, string? failure)
    {
        Cells = cells;
        Cost = cost;
        Expanded = expanded;
        Failure = failure;
    }

    internal List<GridCell>? Cells { get; }

    internal double Cost { get; }

    internal int Expanded { get; }

    /// <summary>no_route, too_long or search_limit; null when a route was found.</summary>
    internal string? Failure { get; }

    internal static RouteResult Found(List<GridCell> cells, double cost, int expanded) =>
        new RouteResult(cells, cost, expanded, null);

    internal static RouteResult Failed(string failure, int expanded) =>
        new RouteResult(null, double.PositiveInfinity, expanded, failure);
}

/// <summary>
/// The cheapest axis-aligned route on the small grid between two ends (A* over cell, heading and axis phase), kind
/// agnostic: what a cell costs or blocks comes from the caller (the family's rules and the survey), never from here.
/// Every step costs the cell it enters; a turn costs BendCost; with an axis order, a step on the second axis before
/// the first is done costs OrderPenalty. The start and goal cells are always enterable.
/// </summary>
internal static class RoutePlanner
{
    internal static RouteResult Find(RouteEnd start, RouteEnd goal, Func<GridCell, CellCost> costOf, RouteRules rules,
        AirBound? air = null) =>
        FindAny(start, new[] { goal }, costOf, rules, air);

    /// <summary>
    /// The cheapest route from the start to whichever goal it reaches most cheaply (a branch joining a tree: every
    /// cell of the tree is a goal). air, under frames_first, adds its lower bound on the air cells still to pay for to
    /// the distance heuristic (it stays admissible, so the route found is still the cheapest).
    /// </summary>
    internal static RouteResult FindAny(RouteEnd start, IReadOnlyList<RouteEnd> goals, Func<GridCell, CellCost> costOf,
        RouteRules rules, AirBound? air = null)
    {
        Dictionary<GridCell, RouteEnd> goalAt = new Dictionary<GridCell, RouteEnd>();
        foreach (RouteEnd end in goals)
        {
            goalAt[end.Cell] = end;
        }

        if (goalAt.ContainsKey(start.Cell))
        {
            return RouteResult.Found(new List<GridCell> { start.Cell }, 0.0, 0);
        }

        Func<GridCell, double> distance = HeuristicFor(goals);
        Func<GridCell, double> heuristic = air == null
            ? distance
            : cell =>
            {
                double cells = distance(cell);
                return cells + air.Extra(cell, cells);
            };

        Dictionary<GridCell, CellCost> costs = new Dictionary<GridCell, CellCost>();
        Dictionary<SearchState, double> best = new Dictionary<SearchState, double>();
        Dictionary<SearchState, SearchState> cameFrom = new Dictionary<SearchState, SearchState>();
        Dictionary<SearchState, int> lengths = new Dictionary<SearchState, int>();
        StateHeap open = new StateHeap();
        SearchState first = new SearchState(start.Cell, -1, false);
        best[first] = 0.0;
        lengths[first] = 1;
        open.Push(first, heuristic(start.Cell));
        int expanded = 0;
        bool tooLong = false;
        while (open.Count > 0)
        {
            SearchState state = open.Pop(out double priority);
            double spent = best[state];
            if (priority > spent + heuristic(state.Cell) + 1e-9)
            {
                continue;
            }

            if (goalAt.ContainsKey(state.Cell))
            {
                return RouteResult.Found(Trace(cameFrom, state), spent, expanded);
            }

            if (++expanded > rules.MaxExpanded)
            {
                return RouteResult.Failed("search_limit", expanded);
            }

            foreach (GridStep step in GridStep.All)
            {
                GridCell next = step.From(state.Cell);
                int length = lengths[state] + 1;
                bool isGoal = goalAt.TryGetValue(next, out RouteEnd? goal);
                if (!rules.Inside(next) && !isGoal)
                {
                    continue;
                }

                if (length > rules.MaxLength)
                {
                    tooLong = true;
                    continue;
                }

                double enter = isGoal ? 1.0 : EnterCost(next, costs, costOf);
                if (double.IsPositiveInfinity(enter) || !Lookup(state.Cell, costs, costOf).Allows(step) ||
                    !Lookup(next, costs, costOf).Allows(step))
                {
                    continue;
                }

                double cost = spent + enter + TurnCost(state, step, rules) + OrderCost(state, step, rules);
                if (state.Heading < 0)
                {
                    double leave = start.CostOf(step);
                    if (double.IsPositiveInfinity(leave))
                    {
                        continue;
                    }

                    cost += leave;
                }

                if (isGoal)
                {
                    cost += goal!.CostOf(step.Opposite);
                }

                SearchState to = new SearchState(next, step.Index, state.Phase || Leaves(step, rules));
                if (best.TryGetValue(to, out double known) && known <= cost)
                {
                    continue;
                }

                best[to] = cost;
                cameFrom[to] = state;
                lengths[to] = length;
                open.Push(to, cost + heuristic(next));
            }
        }

        return RouteResult.Failed(tooLong ? "too_long" : "no_route", expanded);
    }

    private static double EnterCost(GridCell cell, Dictionary<GridCell, CellCost> costs,
        Func<GridCell, CellCost> costOf)
    {
        if (!costs.TryGetValue(cell, out CellCost cost))
        {
            cost = costOf(cell);
            costs[cell] = cost;
        }

        return cost.Passable ? cost.Cost : double.PositiveInfinity;
    }

    // The start and goal cells are always enterable, but what lies along an axis there still counts: a blocked
    // start or goal cell reads as having no blocked axis (its own piece is being joined, not crossed).
    private static CellCost Lookup(GridCell cell, Dictionary<GridCell, CellCost> costs,
        Func<GridCell, CellCost> costOf)
    {
        if (!costs.TryGetValue(cell, out CellCost cost))
        {
            cost = costOf(cell);
            costs[cell] = cost;
        }

        return cost.Passable ? cost : CellCost.Of(1.0);
    }

    private static double TurnCost(SearchState state, GridStep step, RouteRules rules) =>
        state.Heading >= 0 && state.Heading != step.Index ? rules.BendCost : 0.0;

    // The phase flips once the route takes a step off the preferred axis; a preferred step after that costs extra.
    private static double OrderCost(SearchState state, GridStep step, RouteRules rules)
    {
        if (rules.Order == AxisOrder.Any || !state.Phase)
        {
            return 0.0;
        }

        bool preferred = rules.Order == AxisOrder.VerticalFirst ? step.IsVertical : !step.IsVertical;
        return preferred ? rules.OrderPenalty : 0.0;
    }

    private static bool Leaves(GridStep step, RouteRules rules) =>
        rules.Order switch
        {
            AxisOrder.VerticalFirst => !step.IsVertical,
            AxisOrder.HorizontalFirst => step.IsVertical,
            _ => false
        };

    private static double Heuristic(GridCell a, GridCell b) =>
        (Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y) + Math.Abs(a.Z - b.Z)) / (double)GridStep.CellSize;

    private const int ExactGoals = 16;

    // Admissible for several goals: the distance to the nearest one, or for many (a whole network) to the box around
    // them, which is never more and costs nothing per step.
    private static Func<GridCell, double> HeuristicFor(IReadOnlyList<RouteEnd> goals)
    {
        if (goals.Count <= ExactGoals)
        {
            return cell =>
            {
                double nearest = double.PositiveInfinity;
                foreach (RouteEnd goal in goals)
                {
                    nearest = Math.Min(nearest, Heuristic(cell, goal.Cell));
                }

                return nearest;
            };
        }

        int minX = int.MaxValue, minY = int.MaxValue, minZ = int.MaxValue;
        int maxX = int.MinValue, maxY = int.MinValue, maxZ = int.MinValue;
        foreach (RouteEnd goal in goals)
        {
            minX = Math.Min(minX, goal.Cell.X);
            minY = Math.Min(minY, goal.Cell.Y);
            minZ = Math.Min(minZ, goal.Cell.Z);
            maxX = Math.Max(maxX, goal.Cell.X);
            maxY = Math.Max(maxY, goal.Cell.Y);
            maxZ = Math.Max(maxZ, goal.Cell.Z);
        }

        return cell => (Outside(cell.X, minX, maxX) + Outside(cell.Y, minY, maxY) + Outside(cell.Z, minZ, maxZ)) /
                       (double)GridStep.CellSize;
    }

    private static int Outside(int value, int min, int max) => value < min ? min - value : value > max ? value - max : 0;

    private static List<GridCell> Trace(Dictionary<SearchState, SearchState> cameFrom, SearchState end)
    {
        List<GridCell> cells = new List<GridCell> { end.Cell };
        SearchState state = end;
        while (cameFrom.TryGetValue(state, out SearchState previous))
        {
            cells.Add(previous.Cell);
            state = previous;
        }

        cells.Reverse();
        return cells;
    }

    private readonly struct SearchState : IEquatable<SearchState>
    {
        internal SearchState(GridCell cell, int heading, bool phase)
        {
            Cell = cell;
            Heading = heading;
            Phase = phase;
        }

        internal GridCell Cell { get; }

        /// <summary>The GridStep index the route arrived by; -1 at the start.</summary>
        internal int Heading { get; }

        /// <summary>The route has taken a step off the preferred axis (axis order only).</summary>
        internal bool Phase { get; }

        public bool Equals(SearchState other) =>
            Cell.Equals(other.Cell) && Heading == other.Heading && Phase == other.Phase;

        public override bool Equals(object? obj) => obj is SearchState other && Equals(other);

        public override int GetHashCode() => unchecked((Cell.GetHashCode() * 31 + Heading) * 2 + (Phase ? 1 : 0));
    }

    // A binary min-heap on priority; ties keep insertion order out of the picture by comparing priorities only.
    private sealed class StateHeap
    {
        private readonly List<KeyValuePair<double, SearchState>> _items = new List<KeyValuePair<double, SearchState>>();

        internal int Count => _items.Count;

        internal void Push(SearchState state, double priority)
        {
            _items.Add(new KeyValuePair<double, SearchState>(priority, state));
            int index = _items.Count - 1;
            while (index > 0)
            {
                int parent = (index - 1) / 2;
                if (_items[parent].Key <= _items[index].Key)
                {
                    break;
                }

                Swap(index, parent);
                index = parent;
            }
        }

        internal SearchState Pop(out double priority)
        {
            KeyValuePair<double, SearchState> top = _items[0];
            int last = _items.Count - 1;
            _items[0] = _items[last];
            _items.RemoveAt(last);
            int index = 0;
            while (true)
            {
                int left = index * 2 + 1;
                int right = left + 1;
                int smallest = index;
                if (left < _items.Count && _items[left].Key < _items[smallest].Key)
                {
                    smallest = left;
                }

                if (right < _items.Count && _items[right].Key < _items[smallest].Key)
                {
                    smallest = right;
                }

                if (smallest == index)
                {
                    break;
                }

                Swap(index, smallest);
                index = smallest;
            }

            priority = top.Key;
            return top.Value;
        }

        private void Swap(int a, int b)
        {
            KeyValuePair<double, SearchState> item = _items[a];
            _items[a] = _items[b];
            _items[b] = item;
        }
    }
}
