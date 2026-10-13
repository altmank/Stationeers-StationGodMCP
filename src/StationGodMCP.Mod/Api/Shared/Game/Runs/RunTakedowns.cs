#nullable enable

using System.Collections.Generic;
using Assets.Scripts.Objects;

namespace StationGodMCP.Api.Shared.Game.Runs;

/// <summary>What a run job takes down, as its undo record keeps it (JobSnapshots).</summary>
internal static class RunTakedowns
{
    /// <summary>
    /// Its removals (not the assumed ones, not burnt cables) and every piece it changes (a change builds a new piece in
    /// the old one's place): undo puts these back.
    /// </summary>
    internal static List<Structure> Removed(RunPlan plan)
    {
        List<Structure> removed = new List<Structure>();
        foreach (PlannedRemoval removal in plan.Removals)
        {
            if (!removal.Assumed && !removal.Debris)
            {
                removed.Add(removal.Piece);
            }
        }

        foreach (PlannedCell cell in plan.Cells)
        {
            if (cell.Existing != null)
            {
                removed.Add(cell.Existing);
            }
        }

        return removed;
    }

    /// <summary>The burnt cables the run takes down: undo leaves them gone.</summary>
    internal static List<Structure> Burnt(RunPlan plan)
    {
        List<Structure> burnt = new List<Structure>();
        foreach (PlannedRemoval removal in plan.Removals)
        {
            if (!removal.Assumed && removal.Debris)
            {
                burnt.Add(removal.Piece);
            }
        }

        return burnt;
    }
}
