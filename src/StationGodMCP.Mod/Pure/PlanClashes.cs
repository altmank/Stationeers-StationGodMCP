#nullable enable

using System.Collections.Generic;

namespace StationGodMCP.Pure;

/// <summary>
/// Where one placement of a place_structure run would stand once built, as the game's placement checks read it: the
/// small cells a small-grid piece takes with the slot it takes in each (SmallCell keeps one cable, pipe, chute, device,
/// rail and other per cell), or the 2 m cells a large piece registers in (a grid-placed piece's cells; a wall's cell on
/// its front side) and whether it blocks the whole cell (CollisionType.BlockGrid: a frame).
/// </summary>
internal sealed class PlannedFootprint
{
    private PlannedFootprint(int index, string name, IReadOnlyList<GridCell> smallCells, string? slot,
        IReadOnlyList<GridCell> largeCells, bool blocksCell)
    {
        Index = index;
        Name = name;
        SmallCells = smallCells;
        Slot = slot;
        LargeCells = largeCells;
        BlocksCell = blocksCell;
    }

    internal int Index { get; }

    internal string Name { get; }

    internal IReadOnlyList<GridCell> SmallCells { get; }

    /// <summary>The slot a small-grid piece takes in each of its cells; null for a large piece.</summary>
    internal string? Slot { get; }

    internal IReadOnlyList<GridCell> LargeCells { get; }

    internal bool BlocksCell { get; }

    internal static PlannedFootprint Small(int index, string name, IReadOnlyList<GridCell> cells, string slot) =>
        new PlannedFootprint(index, name, cells, slot, new List<GridCell>(), false);

    internal static PlannedFootprint Large(int index, string name, IReadOnlyList<GridCell> cells, bool blocksCell) =>
        new PlannedFootprint(index, name, new List<GridCell>(), null, cells, blocksCell);
}

/// <summary>A later placement the game would refuse once an earlier one of the same run stands.</summary>
internal sealed class PlanClash
{
    internal PlanClash(int index, int blockedBy, string message)
    {
        Index = index;
        BlockedBy = blockedBy;
        Message = message;
    }

    internal int Index { get; }

    internal int BlockedBy { get; }

    internal string Message { get; }
}

/// <summary>
/// The clashes between the placements of one run. The cursor check of a dry run sees only what stands now, while the
/// job checks each piece again just before it builds it, after the earlier ones stand: a small-grid piece is refused
/// where an earlier one takes its slot of a shared cell, and a large piece where an earlier one blocks a 2 m cell it
/// registers in, or where it would block a cell an earlier one registers in (Structure.CanConstructCell: a frame and a
/// wall facing into its cell refuse each other in either order). Each later placement is reported once, against the
/// first earlier one it meets.
/// </summary>
internal static class PlanClashes
{
    internal static List<PlanClash> Find(IReadOnlyList<PlannedFootprint> placements)
    {
        List<PlanClash> clashes = new List<PlanClash>();
        for (int later = 1; later < placements.Count; later++)
        {
            for (int earlier = 0; earlier < later; earlier++)
            {
                string? why = Why(placements[earlier], placements[later]);
                if (why != null)
                {
                    clashes.Add(new PlanClash(placements[later].Index, placements[earlier].Index, why));
                    break;
                }
            }
        }

        return clashes;
    }

    private static string? Why(PlannedFootprint earlier, PlannedFootprint later)
    {
        if (earlier.Slot != null && earlier.Slot == later.Slot && Shared(earlier.SmallCells, later.SmallCells))
        {
            return $"placement {earlier.Index} ({earlier.Name}) takes the {earlier.Slot} slot of a small cell it " +
                   "takes too; the job would stop here";
        }

        if ((earlier.BlocksCell || later.BlocksCell) && Shared(earlier.LargeCells, later.LargeCells))
        {
            return earlier.BlocksCell
                ? $"placement {earlier.Index} ({earlier.Name}) fills a 2 m cell it registers in; the game refuses " +
                  "it once that stands, so the job would stop here"
                : $"it fills a 2 m cell placement {earlier.Index} ({earlier.Name}) registers in; the game refuses " +
                  "it once that stands, so the job would stop here";
        }

        return null;
    }

    private static bool Shared(IReadOnlyList<GridCell> a, IReadOnlyList<GridCell> b)
    {
        if (a.Count == 0 || b.Count == 0)
        {
            return false;
        }

        HashSet<GridCell> cells = new HashSet<GridCell>(a);
        foreach (GridCell cell in b)
        {
            if (cells.Contains(cell))
            {
                return true;
            }
        }

        return false;
    }
}
