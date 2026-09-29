#nullable enable

using System;
using System.Collections.Generic;

namespace StationGodMCP.Pure;

/// <summary>
/// Cells a route search treats as blocked because something else needs them: reserve_cells as given, and the joining
/// cells of reserve_ports (another port of the same device, the next run's end). A reserved cell that is one of the
/// route's own ends is released: blocking it would only hide that end.
/// </summary>
internal sealed class RouteReservation
{
    private readonly HashSet<GridCell> _cells;

    private RouteReservation(HashSet<GridCell> cells, List<GridCell> released)
    {
        _cells = cells;
        Released = released;
    }

    internal static RouteReservation None { get; } =
        new RouteReservation(new HashSet<GridCell>(), new List<GridCell>());

    /// <summary>The cells kept free.</summary>
    internal int Count => _cells.Count;

    /// <summary>Reserved cells that are the route's own ends, so not blocked.</summary>
    internal List<GridCell> Released { get; }

    internal static RouteReservation Of(IEnumerable<GridCell> reserved, IEnumerable<GridCell> ends)
    {
        HashSet<GridCell> cells = new HashSet<GridCell>(reserved);
        List<GridCell> released = new List<GridCell>();
        foreach (GridCell end in ends)
        {
            if (cells.Remove(end))
            {
                released.Add(end);
            }
        }

        return new RouteReservation(cells, released);
    }

    internal bool Contains(GridCell cell) => _cells.Contains(cell);

    /// <summary>The cost function with every reserved cell blocked.</summary>
    internal Func<GridCell, CellCost> Guard(Func<GridCell, CellCost> cost) =>
        _cells.Count == 0 ? cost : cell => _cells.Contains(cell) ? CellCost.Blocked : cost(cell);
}

/// <summary>
/// Which free end of a thing a route starts or ends at: the one named (port), else the only free one. A candidate is
/// an end of the route's kind; free when nothing is joined to it.
/// </summary>
internal static class EndChoice
{
    /// <summary>The chosen end's index, or null with the reason in error.</summary>
    internal static int? Pick(IReadOnlyList<(int Index, bool Free)> candidates, int? port, out string? error)
    {
        List<int> all = new List<int>();
        List<int> free = new List<int>();
        foreach ((int index, bool isFree) in candidates)
        {
            all.Add(index);
            if (isFree)
            {
                free.Add(index);
            }
        }

        error = null;
        if (port.HasValue)
        {
            if (!all.Contains(port.Value))
            {
                error = $"end {port.Value} is not one of its ends of that kind [{string.Join(", ", all)}]";
                return null;
            }

            if (!free.Contains(port.Value))
            {
                error = $"end {port.Value} is already joined; its free ends are [{string.Join(", ", free)}]";
                return null;
            }

            return port.Value;
        }

        if (free.Count == 1)
        {
            return free[0];
        }

        error = free.Count == 0
            ? $"it has no free end (ends [{string.Join(", ", all)}] are all joined)"
            : $"it has {free.Count} free ends [{string.Join(", ", free)}]; name one with port";
        return null;
    }
}

/// <summary>
/// The two cells of a thing's end: the one inside the thing and the one beyond it, where a piece joining that end
/// stands. An end's transform sits on the thing's face, so which of Connection.GetLocalGrid and GetFacingGrid falls
/// inside depends on the prefab (a pipe's ends and a 1x2 in-line tank's: local beyond, facing inside); the cell the
/// thing occupies decides. Local counts as beyond when neither or both lie inside.
/// </summary>
internal static class EndCells
{
    internal static (GridCell Inside, GridCell Beyond) Of(PieceModel thing, GridCell local, GridCell facing) =>
        thing.Occupies(local) && !thing.Occupies(facing) ? (local, facing) : (facing, local);
}
