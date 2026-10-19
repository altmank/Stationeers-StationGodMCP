#nullable enable

using System;
using System.Collections.Generic;

namespace StationGodMCP.Pure;

/// <summary>
/// controls_reach, a first version of the walk to a device's controls: a walk over a room's 2 m cells from the cells
/// inside its doors to the 2 m cell in front of the controls. A cell is walkable when it is in the room, holds no frame
/// and has a 1 m wide free column: two by two small columns (x, z) whose four small cells up (a 2 m cell's height) hold
/// no device, chute or other small-grid thing ('d', 'h', 'o' in survey's small code). Cables and pipes are taken as
/// walked over. A step crosses a face with no wall or window on it (a door passes), up and down as well as across:
/// the walk takes a ladder or stairs to stand wherever a floor is open. Not modelled yet: that the free columns of
/// neighbouring cells line up, a floor to stand on, and the player's own height against low ceilings.
/// </summary>
internal static class WalkReach
{
    /// <summary>A 2 m cell's 64 small cells (survey's small code) leave a 1 m wide column free from floor to top.</summary>
    internal static bool HasClearColumn(string small)
    {
        for (int x0 = 0; x0 < SmallCellCode.PerAxis - 1; x0++)
        {
            for (int z0 = 0; z0 < SmallCellCode.PerAxis - 1; z0++)
            {
                if (ColumnClear(small, x0, z0))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool ColumnClear(string small, int x0, int z0)
    {
        for (int x = x0; x <= x0 + 1; x++)
        {
            for (int z = z0; z <= z0 + 1; z++)
            {
                for (int y = 0; y < SmallCellCode.PerAxis; y++)
                {
                    if (Blocks(small[x + SmallCellCode.PerAxis * y + SmallCellCode.PerAxis * SmallCellCode.PerAxis * z]))
                    {
                        return false;
                    }
                }
            }
        }

        return true;
    }

    private static bool Blocks(char cell) => cell is 'd' or 'h' or 'o';

    /// <summary>The 2 m cell beside a 2 m cell, one step away.</summary>
    internal static GridCell Beside(GridCell large, GridStep step) =>
        new GridCell(large.X + step.Dx * SmallCellCode.Large, large.Y + step.Dy * SmallCellCode.Large,
            large.Z + step.Dz * SmallCellCode.Large);

    /// <summary>
    /// Whether a walk from any walkable start reaches the target over walkable cells, each step across a face crossable
    /// says may be crossed (a face of the cell stepped from, toward the step).
    /// </summary>
    internal static bool Reaches(IEnumerable<GridCell> starts, GridCell target, Func<GridCell, bool> walkable,
        Func<GridCell, GridStep, bool> crossable)
    {
        HashSet<GridCell> seen = new HashSet<GridCell>();
        Queue<GridCell> queue = new Queue<GridCell>();
        foreach (GridCell start in starts)
        {
            if (seen.Add(start) && walkable(start))
            {
                queue.Enqueue(start);
            }
        }

        while (queue.Count > 0)
        {
            GridCell cell = queue.Dequeue();
            if (cell.Equals(target))
            {
                return true;
            }

            foreach (GridStep step in GridStep.All)
            {
                GridCell next = Beside(cell, step);
                if (!seen.Contains(next) && crossable(cell, step) && walkable(next))
                {
                    seen.Add(next);
                    queue.Enqueue(next);
                }
            }
        }

        return false;
    }
}
