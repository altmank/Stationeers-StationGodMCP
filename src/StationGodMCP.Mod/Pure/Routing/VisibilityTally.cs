#nullable enable

using System;
using System.Collections.Generic;

namespace StationGodMCP.Pure;

/// <summary>Cells counted by how visible a piece in them is (CellVisibility), with the air cells kept in order.</summary>
internal sealed class VisibilityTally
{
    private readonly int[] _counts = new int[4];

    internal int Inside => _counts[(int)CellVisibility.Inside];

    internal int FrameSurface => _counts[(int)CellVisibility.FrameSurface];

    internal int Wall => _counts[(int)CellVisibility.Wall];

    internal int Air => _counts[(int)CellVisibility.Air];

    internal int Total => Inside + FrameSurface + Wall + Air;

    /// <summary>The cells in air, in the order they were added.</summary>
    internal List<GridCell> AirCells { get; } = new List<GridCell>();

    internal void Add(GridCell cell, CellVisibility visibility)
    {
        _counts[(int)visibility]++;
        if (visibility == CellVisibility.Air)
        {
            AirCells.Add(cell);
        }
    }

    /// <summary>Each cell once, by the visibility the function gives it.</summary>
    internal static VisibilityTally Of(IEnumerable<GridCell> cells, Func<GridCell, CellVisibility> visibility)
    {
        VisibilityTally tally = new VisibilityTally();
        HashSet<GridCell> seen = new HashSet<GridCell>();
        foreach (GridCell cell in cells)
        {
            if (seen.Add(cell))
            {
                tally.Add(cell, visibility(cell));
            }
        }

        return tally;
    }
}

/// <summary>Which of the pieces assumed removed stand in cells a new route takes (they must go before it is built).</summary>
internal static class RemovedInTheWay
{
    internal static List<long> Of(IEnumerable<GridCell> route, IReadOnlyDictionary<long, IReadOnlyList<GridCell>> pieces)
    {
        HashSet<GridCell> cells = new HashSet<GridCell>(route);
        List<long> ids = new List<long>();
        foreach (KeyValuePair<long, IReadOnlyList<GridCell>> piece in pieces)
        {
            foreach (GridCell cell in piece.Value)
            {
                if (cells.Contains(cell))
                {
                    ids.Add(piece.Key);
                    break;
                }
            }
        }

        ids.Sort();
        return ids;
    }
}
