#nullable enable

using System;
using System.Collections.Generic;

namespace StationGodMCP.Pure;

/// <summary>The 2 m cells of the grid, read from points in metres.</summary>
internal static class LargeCells
{
    private const double Edge = 1e-6;

    /// <summary>
    /// The 2 m cell holding a point (its centre, decimetres). A point on a face plane belongs to the cell on its plus
    /// side, as the grid gives a cell's first small cell to its minimum plane (SmallCellCode.LargeOf).
    /// </summary>
    internal static GridCell Containing(Vec3 point) =>
        new GridCell(Centre(point.X), Centre(point.Y), Centre(point.Z));

    /// <summary>
    /// The 2 m cell the game looks in for the frame a piece stands on: depth metres below its origin, along its up
    /// (SmallGrid.HasFrameBelow: half the piece's grid size; LargeElectrical and LandingPadModular: all of it).
    /// </summary>
    internal static GridCell Below(Vec3 origin, Vec3 up, double depth) => Containing(origin - up * depth);

    /// <summary>
    /// The 2 m cells a box overlaps (corners in any order), y then z then x ascending. A side of the box lying on a face
    /// plane takes no cell beyond it, so a box up to y 222 stops at the cell below that floor; a box flat on an axis
    /// takes the cell its coordinate belongs to.
    /// </summary>
    internal static List<GridCell> InBox(Vec3 a, Vec3 b)
    {
        (int x0, int x1) = Span(a.X, b.X);
        (int y0, int y1) = Span(a.Y, b.Y);
        (int z0, int z1) = Span(a.Z, b.Z);
        List<GridCell> cells = new List<GridCell>();
        for (int y = y0; y <= y1; y += CellDm)
        {
            for (int z = z0; z <= z1; z += CellDm)
            {
                for (int x = x0; x <= x1; x += CellDm)
                {
                    cells.Add(new GridCell(x, y, z));
                }
            }
        }

        return cells;
    }

    /// <summary>How many 2 m cells InBox lists, without listing them.</summary>
    internal static long CountInBox(Vec3 a, Vec3 b)
    {
        (int x0, int x1) = Span(a.X, b.X);
        (int y0, int y1) = Span(a.Y, b.Y);
        (int z0, int z1) = Span(a.Z, b.Z);
        return (long)((x1 - x0) / CellDm + 1) * ((y1 - y0) / CellDm + 1) * ((z1 - z0) / CellDm + 1);
    }

    private const int CellDm = 20;

    // The first and last cell centres (decimetres) along one axis.
    private static (int First, int Last) Span(double a, double b)
    {
        double low = Math.Min(a, b);
        double high = Math.Max(a, b);
        int first = Centre(low);
        int last = Centre(high);
        bool onPlane = Math.Abs(high / 2.0 - Math.Round(high / 2.0)) < Edge;
        return (first, onPlane && high > low + Edge && last > first ? last - CellDm : last);
    }

    private static int Centre(double metres) =>
        (int)Math.Round((Math.Floor(metres / 2.0 + Edge) * 2.0 + 1.0) * 10.0);
}
