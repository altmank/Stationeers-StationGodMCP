#nullable enable

using System;
using System.Collections.Generic;
using System.Text;

namespace StationGodMCP.Pure;

/// <summary>What a 2 m face shows behind the small cells in front of it.</summary>
internal enum FaceLook
{
    /// <summary>Nothing: open to the other side.</summary>
    Open,

    Wall,

    Window,

    Door,

    /// <summary>No face structure, but a frame right behind it.</summary>
    Frame
}

/// <summary>One small cell of a wall map: the face behind it, what stands in it, and whether a door keeps it out.</summary>
internal readonly struct WallCell
{
    internal WallCell(FaceLook face, char thing, bool keepOut)
    {
        Face = face;
        Thing = thing;
        KeepOut = keepOut;
    }

    internal FaceLook Face { get; }

    /// <summary>What stands in the cell or right in front of it: a device's key letter, c, p, b, h; '\0' for nothing.</summary>
    internal char Thing { get; }

    internal bool KeepOut { get; }

    internal bool IsFree => Thing == '\0' && !KeepOut && Face != FaceLook.Door;

    /// <summary>A body's key stands in it (not a run character, not nothing).</summary>
    internal bool HoldsBody => Thing != '\0' && !IsRunCharacter(Thing);

    /// <summary>
    /// The cell with a body's mesh over it: the body's key replaces a run character or nothing (a mesh hides what runs
    /// behind it); a body already keyed there (registered in the cell, or met first) stays.
    /// </summary>
    internal WallCell WithBody(char key) => HoldsBody ? this : new WallCell(Face, key, KeepOut);

    private static bool IsRunCharacter(char thing) => thing == 'c' || thing == 'p' || thing == 'b' || thing == 'h';

    /// <summary>
    /// The map character: the thing when one stands there, 'x' in a door's keep-out, else the face: W wall, G window,
    /// D door, F frame, '.' open.
    /// </summary>
    internal char Symbol =>
        Thing != '\0' ? Thing
        : Face == FaceLook.Door ? 'D'
        : KeepOut ? 'x'
        : Face switch
        {
            FaceLook.Wall => 'W',
            FaceLook.Window => 'G',
            FaceLook.Frame => 'F',
            _ => '.'
        };
}

/// <summary>
/// A text elevation of one face plane as seen from one side: columns left to right and rows top to bottom as the viewer
/// sees them, one per 0.5 m small cell. Column c is the small cell at U(c) along the plane's horizontal axis (or the
/// viewer's right on a floor), row r at V(r) along its vertical. Seams (2 m face edges, even metres) are marked in a
/// ruler row; free_rects lists where a w x h rectangle fits on free cells.
/// </summary>
internal sealed class WallMap
{
    private readonly WallCell[,] _cells;

    /// <param name="uStart">Metres of column 0.</param>
    /// <param name="uStep">+0.5 when the viewer's right runs along +u, -0.5 when along -u.</param>
    /// <param name="vTop">Metres of row 0 (the top).</param>
    /// <param name="cells">[row, column].</param>
    internal WallMap(double uStart, double uStep, double vTop, WallCell[,] cells)
    {
        UStart = uStart;
        UStep = uStep;
        VTop = vTop;
        _cells = cells;
    }

    internal double UStart { get; }

    internal double UStep { get; }

    internal double VTop { get; }

    internal int Rows => _cells.GetLength(0);

    internal int Columns => _cells.GetLength(1);

    internal double U(int column) => UStart + column * UStep;

    internal double V(int row) => VTop - row * 0.5;

    internal WallCell At(int row, int column) => _cells[row, column];

    /// <summary>The map as text: a ruler row marking seams with '|', then one row per 0.5 m, top first.</summary>
    internal List<string> Lines()
    {
        List<string> lines = new List<string>(Rows + 1);
        StringBuilder ruler = new StringBuilder(Columns);
        for (int column = 0; column < Columns; column++)
        {
            ruler.Append(IsSeam(U(column)) ? '|' : ' ');
        }

        lines.Add(ruler.ToString());
        for (int row = 0; row < Rows; row++)
        {
            StringBuilder line = new StringBuilder(Columns);
            for (int column = 0; column < Columns; column++)
            {
                line.Append(_cells[row, column].Symbol);
            }

            lines.Add(line.ToString());
        }

        return lines;
    }

    /// <summary>Whether a coordinate lies on a 2 m seam (an even metre).</summary>
    internal static bool IsSeam(double metres) => Math.Abs(metres / 2.0 - Math.Round(metres / 2.0)) < 1e-6;

    /// <summary>
    /// Where a rectangle of cellsWide x cellsHigh small cells fits with every cell free: its top-left row and column, by
    /// rows from the top. With oneSection a rectangle may not straddle a seam (its cells span no even metre except at
    /// its outer edges); requireWall keeps it on wall (not window, frame or open) cells. At most limit.
    /// </summary>
    internal List<(int Row, int Column)> FreeRects(int cellsWide, int cellsHigh, bool oneSection, bool requireWall,
        int limit)
    {
        List<(int, int)> found = new List<(int, int)>();
        for (int row = 0; row + cellsHigh <= Rows && found.Count < limit; row++)
        {
            for (int column = 0; column + cellsWide <= Columns && found.Count < limit; column++)
            {
                if (Fits(row, column, cellsWide, cellsHigh, oneSection, requireWall))
                {
                    found.Add((row, column));
                }
            }
        }

        return found;
    }

    private bool Fits(int row, int column, int wide, int high, bool oneSection, bool requireWall)
    {
        for (int r = row; r < row + high; r++)
        {
            for (int c = column; c < column + wide; c++)
            {
                WallCell cell = _cells[r, c];
                if (!cell.IsFree || (requireWall && cell.Face != FaceLook.Wall))
                {
                    return false;
                }
            }
        }

        if (!oneSection)
        {
            return true;
        }

        double first = Math.Min(U(column), U(column + wide - 1)) - 0.25;
        double last = Math.Max(U(column), U(column + wide - 1)) + 0.25;
        double top = V(row) + 0.25;
        double bottom = V(row + high - 1) - 0.25;
        return MountRect.Centres(first, last).Count == 1 && MountRect.Centres(bottom, top).Count == 1;
    }
}

/// <summary>
/// Where a wall map's coordinates lie in the world: u along the viewer's right axis, v along the up axis, both in
/// metres, on a face plane seen from one side.
/// </summary>
internal static class PlaneCells
{
    /// <summary>The world point at (u, v) on the plane.</summary>
    internal static Vec3 PointAt(FacePlane plane, int rightAxis, int upAxis, double u, double v) =>
        Vec3.Zero.With(plane.Axis, plane.Metres).With(rightAxis, u).With(upAxis, v);

    /// <summary>
    /// The box one map character covers: the 0.5 m small cell centred on the plane at (u, v) and the one in front of
    /// it on the viewer's side.
    /// </summary>
    internal static Box3 CellBox(FacePlane plane, GridStep side, int rightAxis, int upAxis, double u, double v)
    {
        Vec3 centre = PointAt(plane, rightAxis, upAxis, u, v);
        Vec3 half = new Vec3(0.25, 0.25, 0.25);
        Vec3 front = Vec3.Of(side) * 0.5;
        return Box3.Around(new[] { centre - half, centre + half, centre - half + front, centre + half + front });
    }

    /// <summary>
    /// The centre (decimetres) of the 2 m section holding the small cell at a coordinate (metres) along the plane: a
    /// small cell on a seam belongs to the section on its plus side, the grid's rule (SmallCellCode.LargeOf).
    /// </summary>
    internal static int FaceCentre(double metres) => (int)(Math.Floor(metres / 2.0 + 1e-6) * 20 + 10);

    /// <summary>Whether a mesh box covers a map character's cells by more than the clash tolerance (VisualClash).</summary>
    internal static bool Covers(Box3 render, Box3 cell) => VisualClash.Clashes(render, cell);
}
