#nullable enable

using System.Collections.Generic;

namespace StationGodMCP.Pure;

/// <summary>What a piece's connected ends make of it.</summary>
internal enum EndUse
{
    /// <summary>Every end is connected (or it has none): the piece is already the smallest that serves.</summary>
    AllConnected,

    /// <summary>Two or more ends connected and at least one open: a piece with only those ends would do.</summary>
    Shrink,

    /// <summary>One end is connected: a stub that feeds nothing further. Reported, never removed.</summary>
    DeadEnd,

    /// <summary>No end is connected. Reported, never removed.</summary>
    Isolated
}

/// <summary>
/// clean_cables' rule on plain values: which pieces carry open ends, and the piece that would replace one (the same
/// cells, only its connected ends). Whether such a piece exists is the coil's to say (TwinFinder).
/// </summary>
internal static class EndCleanup
{
    internal static EndUse Of(int ends, int connected)
    {
        if (connected >= ends)
        {
            return EndUse.AllConnected;
        }

        return connected switch
        {
            0 => EndUse.Isolated,
            1 => EndUse.DeadEnd,
            _ => EndUse.Shrink
        };
    }

    /// <summary>The piece with the same id, cells and content, and only the given ends.</summary>
    internal static PieceModel WithEnds(PieceModel piece, IReadOnlyList<PieceEnd> ends) =>
        new PieceModel(piece.Id, piece.Cells, ends, piece.Content);

    /// <summary>
    /// The world axis an end points along, as "+x", "-y" and so on: from the cell it faces (inside its own piece) to
    /// the cell it sits in (the neighbour's). Cells are in the grid's own units (a small-grid cell is 5 of them), so
    /// only the sign of the one changing axis counts; a step along more than one axis is given as the step itself.
    /// </summary>
    internal static string DirectionOf(PieceEnd end)
    {
        int x = end.Local.X - end.Facing.X;
        int y = end.Local.Y - end.Facing.Y;
        int z = end.Local.Z - end.Facing.Z;
        int axes = (x != 0 ? 1 : 0) + (y != 0 ? 1 : 0) + (z != 0 ? 1 : 0);
        if (axes != 1)
        {
            return $"({x}, {y}, {z})";
        }

        string axis = x != 0 ? "x" : y != 0 ? "y" : "z";
        return (x + y + z > 0 ? "+" : "-") + axis;
    }

    /// <summary>Each end's direction, in order.</summary>
    internal static List<string> DirectionsOf(IReadOnlyList<PieceEnd> ends)
    {
        List<string> directions = new List<string>(ends.Count);
        for (int index = 0; index < ends.Count; index++)
        {
            directions.Add(DirectionOf(ends[index]));
        }

        return directions;
    }

    /// <summary>
    /// What swapping one coil piece for another costs, as the coil's own merge placement charges it
    /// (MultiMergeConstructor.Construct: the new piece's entry quantity less the old one's). Positive: coils taken;
    /// negative: that many given back.
    /// </summary>
    internal static int CostDifference(int oldEntryQuantity, int newEntryQuantity) =>
        newEntryQuantity - oldEntryQuantity;
}
