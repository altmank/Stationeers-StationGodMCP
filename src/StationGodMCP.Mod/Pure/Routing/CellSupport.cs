#nullable enable

using System;

namespace StationGodMCP.Pure;

/// <summary>What holds a small cell's piece up, from the frames and walls around it.</summary>
internal enum CellSupport
{
    /// <summary>Not inside or on the surface of any frame cell and on no wall's plane: the piece would float.</summary>
    Air,

    /// <summary>On the plane of a wall or window (not touching a frame).</summary>
    Wall,

    /// <summary>Inside a frame cell or on one of its faces.</summary>
    Frame,

    /// <summary>On two or three face planes of a frame cell: its edges and corners.</summary>
    FrameEdge
}

/// <summary>
/// Classifies small cells by what supports them. A small cell centre is a point of the small grid; a 2 m cell is a
/// closed box of side 20 (Grid3 units) around its centre. The large cells a small cell touches are the ones whose
/// closed box holds its point: its own, and along each axis where it lies on its own cell's minimum face plane (index
/// 0) also the neighbour on the minus side, so up to eight. The cell is on a frame when one of those holds a frame
/// (inside it or on its surface: the top of a frame beam is its own cell's y-minimum plane, and that cell above has
/// no frame), on a frame edge when it lies on two or three face planes of such a frame cell, and on a wall when one
/// of those cells carries a face structure on a face whose plane holds the point. Anything else is air.
/// </summary>
internal static class CellSupports
{
    private const int Half = SmallCellCode.Large / 2;

    internal static CellSupport Of(GridCell small, Func<GridCell, LargeCellFacts> large)
    {
        GridCell own = SmallCellCode.LargeOf(small);
        int lastX = small.X == own.X - Half ? 1 : 0;
        int lastY = small.Y == own.Y - Half ? 1 : 0;
        int lastZ = small.Z == own.Z - Half ? 1 : 0;
        CellSupport best = CellSupport.Air;
        for (int x = 0; x <= lastX; x++)
        {
            for (int y = 0; y <= lastY; y++)
            {
                for (int z = 0; z <= lastZ; z++)
                {
                    GridCell touched = new GridCell(own.X - x * SmallCellCode.Large, own.Y - y * SmallCellCode.Large,
                        own.Z - z * SmallCellCode.Large);
                    best = Stronger(best, By(small, touched, large(touched)));
                    if (best == CellSupport.FrameEdge)
                    {
                        return best;
                    }
                }
            }
        }

        return best;
    }

    /// <summary>
    /// Whether a large cell can support any small cell (a frame in it, or a face structure on one of its faces):
    /// every supported small cell lies in the closed box of such a cell.
    /// </summary>
    internal static bool Anchors(LargeCellFacts facts) => facts.Frame || facts.WallFaces != 0;

    // What one touched large cell gives the small cell.
    private static CellSupport By(GridCell small, GridCell large, LargeCellFacts facts)
    {
        if (facts.Frame)
        {
            return PlanesOf(small, large) >= 2 ? CellSupport.FrameEdge : CellSupport.Frame;
        }

        return facts.WallFaces != 0 && OnWalledFace(small, large, facts) ? CellSupport.Wall : CellSupport.Air;
    }

    private static int PlanesOf(GridCell small, GridCell large) =>
        OnFace(small.X, large.X) + OnFace(small.Y, large.Y) + OnFace(small.Z, large.Z);

    private static int OnFace(int coordinate, int centre) =>
        coordinate == centre - Half || coordinate == centre + Half ? 1 : 0;

    private static bool OnWalledFace(GridCell small, GridCell large, LargeCellFacts facts) =>
        OnWalledFace(small.X, large.X, 0, facts) || OnWalledFace(small.Y, large.Y, 1, facts) ||
        OnWalledFace(small.Z, large.Z, 2, facts);

    // On the cell's maximum face along the axis with a face structure there (GridStep.All[axis * 2]), or on its
    // minimum face with one there (GridStep.All[axis * 2 + 1]).
    private static bool OnWalledFace(int coordinate, int centre, int axis, LargeCellFacts facts) =>
        (coordinate == centre + Half && facts.HasWall(GridStep.All[axis * 2])) ||
        (coordinate == centre - Half && facts.HasWall(GridStep.All[axis * 2 + 1]));

    private static CellSupport Stronger(CellSupport a, CellSupport b) => a >= b ? a : b;

    /// <summary>A large cell's 64 small cells' support as one string, index x + 4y + 16z (as SmallCellCode).</summary>
    internal static string Encode(GridCell large, Func<GridCell, CellSupport> read)
    {
        char[] text = new char[SmallCellCode.PerCell];
        for (int index = 0; index < SmallCellCode.PerCell; index++)
        {
            text[index] = Code(read(SmallCellCode.SmallAt(large, index)));
        }

        return new string(text);
    }

    /// <summary>grid_survey's character: 'e' frame edge or corner, 'f' on or inside a frame, 'w' wall plane, 'a' air.</summary>
    internal static char Code(CellSupport support) =>
        support switch
        {
            CellSupport.FrameEdge => 'e',
            CellSupport.Frame => 'f',
            CellSupport.Wall => 'w',
            _ => 'a'
        };
}
