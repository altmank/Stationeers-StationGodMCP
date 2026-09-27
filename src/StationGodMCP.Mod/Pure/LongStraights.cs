#nullable enable

using System.Collections.Generic;

namespace StationGodMCP.Pure;

/// <summary>
/// A long straight (the 3-, 5- and 10-long cable and pipe pieces) as the single straights that would cover it, on
/// plain values. A piece is a long straight when it occupies two or more cells in one line, evenly spaced along one
/// axis, and has exactly two ends of one type and role: one at each tip, pointing out along the line. Each single
/// then stands in one of those cells with an end towards each neighbour along the line; the tip ones keep the long
/// piece's own tip ends. Every part keeps the long piece's id and content, so links among the parts are not links at
/// all (Connectivity ignores a piece linking to itself) and the links out of the run are exactly the long piece's.
/// </summary>
internal static class LongStraights
{
    /// <summary>The singles, in order along the line; null when the piece is not a long straight.</summary>
    internal static List<PieceModel>? Split(PieceModel piece)
    {
        List<GridCell>? line = Line(piece.Cells);
        if (line == null || piece.Ends.Count != 2)
        {
            return null;
        }

        PieceEnd first = piece.Ends[0];
        PieceEnd second = piece.Ends[1];
        if (first.Type != second.Type || first.Role != second.Role || !TipsOut(line, first, second))
        {
            return null;
        }

        List<PieceModel> parts = new List<PieceModel>(line.Count);
        for (int index = 0; index < line.Count; index++)
        {
            GridCell cell = line[index];
            GridCell before = index > 0 ? line[index - 1] : Step(cell, line[1], -1);
            GridCell after = index < line.Count - 1 ? line[index + 1] : Step(line[index - 1], cell, 2);
            PieceEnd back = new PieceEnd(before, cell, first.Type, first.Role);
            PieceEnd ahead = new PieceEnd(after, cell, first.Type, first.Role);
            parts.Add(new PieceModel(piece.Id, new[] { cell }, new[] { back, ahead }, piece.Content));
        }

        return parts;
    }

    // The cells sorted along their one changing axis, evenly spaced; null when they are not such a line.
    private static List<GridCell>? Line(IReadOnlyList<GridCell> cells)
    {
        if (cells.Count < 2)
        {
            return null;
        }

        List<GridCell> line = new List<GridCell>(cells);
        line.Sort(static (a, b) =>
            a.X != b.X ? a.X.CompareTo(b.X) : a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.Z.CompareTo(b.Z));
        GridCell step = Difference(line[0], line[1]);
        int axes = (step.X != 0 ? 1 : 0) + (step.Y != 0 ? 1 : 0) + (step.Z != 0 ? 1 : 0);
        if (axes != 1)
        {
            return null;
        }

        for (int index = 2; index < line.Count; index++)
        {
            if (!Difference(line[index - 1], line[index]).Equals(step))
            {
                return null;
            }
        }

        return line;
    }

    // One end sits one step beyond each tip and faces back into it.
    private static bool TipsOut(List<GridCell> line, PieceEnd first, PieceEnd second)
    {
        GridCell start = line[0];
        GridCell end = line[line.Count - 1];
        PieceEnd startOut = new PieceEnd(Step(start, line[1], -1), start, first.Type, first.Role);
        PieceEnd endOut = new PieceEnd(Step(line[line.Count - 2], end, 2), end, first.Type, first.Role);
        return (first.Equals(startOut) && second.Equals(endOut)) || (first.Equals(endOut) && second.Equals(startOut));
    }

    private static GridCell Difference(GridCell from, GridCell to) =>
        new GridCell(to.X - from.X, to.Y - from.Y, to.Z - from.Z);

    // from + times * (to - from).
    private static GridCell Step(GridCell from, GridCell to, int times) =>
        new GridCell(from.X + times * (to.X - from.X), from.Y + times * (to.Y - from.Y),
            from.Z + times * (to.Z - from.Z));
}
