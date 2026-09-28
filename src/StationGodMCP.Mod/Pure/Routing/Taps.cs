#nullable enable

using System;
using System.Collections.Generic;

namespace StationGodMCP.Pure;

/// <summary>
/// A piece near a run that the run does not join: the run cell it is near, the direction, how many free cells lie
/// between (0: next to it, 1: one cell short), the piece, and the cells a tap would add to join it (the free cells,
/// then the piece's own cell, which the layout turns into a junction).
/// </summary>
internal sealed class NearMiss
{
    internal NearMiss(GridCell from, GridStep step, int gap, long piece, List<GridCell> tap, bool outward)
    {
        From = from;
        Step = step;
        Gap = gap;
        Piece = piece;
        Tap = tap;
        Outward = outward;
    }

    internal GridCell From { get; }

    internal GridStep Step { get; }

    internal int Gap { get; }

    internal long Piece { get; }

    internal List<GridCell> Tap { get; }

    /// <summary>Straight on from the tip (away from the run), where its open end already points.</summary>
    internal bool Outward { get; }
}

/// <summary>
/// The tap check: where a run stops short of a piece it should join. From a tip (a run end), every direction but back
/// into the run is looked along: a piece in the next cell (gap 0, an end that does not match), or in the cell after a
/// free one (gap 1, one cell short). From any other run cell only the next cell counts, and only for a named target
/// network. Pieces the run joins after the edit are never near misses; the caller's lookup leaves them out.
/// </summary>
internal static class Taps
{
    /// <summary>
    /// The near misses from a tip, best first: fewer free cells, then straight on, then direction order.
    /// pieceAt: the id of an unjoined piece of interest in a cell, else null; occupied: whether any piece of the kind
    /// (joined or not) stands in a cell, so a tap never passes through one.
    /// </summary>
    internal static List<NearMiss> FromTip(GridCell tip, GridCell? behind, Func<GridCell, long?> pieceAt,
        Func<GridCell, bool> occupied)
    {
        GridStep? back = behind.HasValue ? GridStep.Between(tip, behind.Value) : null;
        List<NearMiss> misses = new List<NearMiss>();
        foreach (GridStep step in GridStep.All)
        {
            if (back.HasValue && step.Equals(back.Value))
            {
                continue;
            }

            bool outward = back.HasValue && step.Equals(back.Value.Opposite);
            GridCell next = step.From(tip);
            long? adjacent = pieceAt(next);
            if (adjacent.HasValue)
            {
                misses.Add(new NearMiss(tip, step, 0, adjacent.Value, new List<GridCell> { next }, outward));
                continue;
            }

            if (occupied(next))
            {
                continue;
            }

            GridCell after = step.From(tip, 2);
            long? beyond = pieceAt(after);
            if (beyond.HasValue)
            {
                misses.Add(new NearMiss(tip, step, 1, beyond.Value, new List<GridCell> { next, after }, outward));
            }
        }

        misses.Sort(static (a, b) => a.Gap != b.Gap ? a.Gap.CompareTo(b.Gap)
            : a.Outward != b.Outward ? (a.Outward ? -1 : 1)
            : a.Step.Index.CompareTo(b.Step.Index));
        return misses;
    }

    /// <summary>Pieces of interest next to any of the cells (gap 0), one per cell and piece.</summary>
    internal static List<NearMiss> Beside(IReadOnlyList<GridCell> cells, Func<GridCell, long?> pieceAt)
    {
        HashSet<GridCell> own = new HashSet<GridCell>(cells);
        List<NearMiss> misses = new List<NearMiss>();
        HashSet<long> seen = new HashSet<long>();
        foreach (GridCell cell in cells)
        {
            foreach (GridStep step in GridStep.All)
            {
                GridCell next = step.From(cell);
                if (own.Contains(next))
                {
                    continue;
                }

                long? piece = pieceAt(next);
                if (piece.HasValue && seen.Add(piece.Value))
                {
                    misses.Add(new NearMiss(cell, step, 0, piece.Value, new List<GridCell> { next }, false));
                }
            }
        }

        return misses;
    }

    /// <summary>
    /// The one tap to add, from the near misses of every tip in the tips' order (the caller lists the main run's last
    /// tip first): the fewest free cells, then straight on, then the earlier tip. Null when there is none.
    /// </summary>
    internal static NearMiss? Best(IReadOnlyList<List<NearMiss>> byTip)
    {
        NearMiss? best = null;
        foreach (List<NearMiss> misses in byTip)
        {
            foreach (NearMiss miss in misses)
            {
                if (best == null || miss.Gap < best.Gap || (miss.Gap == best.Gap && miss.Outward && !best.Outward))
                {
                    best = miss;
                }
            }
        }

        return best;
    }
}
