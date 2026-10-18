#nullable enable

using System;

namespace StationGodMCP.Pure;

/// <summary>
/// Which cell a point names for a face-placed piece (PlacementSnap.Face: elevator shafts and levels, and the like). The
/// game files a standing face-placed piece under the cell its front faces into: its own position nudged 0.1 m along its
/// forward, then snapped to the cell centre (Structure.GetGrid, Structure.cs:1208-1210), and the piece itself stands on
/// that cell's face, half a cell against its forward (the cursor's snap). A point read as it is would put a piece's own
/// position on the face between two cells, where the snap (Mathf.Round, which rounds a half to the even number,
/// ExtensionMethods.GridCenter, ExtensionMethods.cs:1092-1100) picks either cell depending on the coordinate: at x
/// -848 with the front toward -x the piece landed at -846, 2 m off. So the point is nudged first, as GetGrid nudges:
/// any point on that face, a standing piece's position included, names the cell in front of it and gives that piece's
/// position back; a point inside a cell names that cell, as before.
/// </summary>
internal static class FacePlacement
{
    internal const double Nudge = 0.1;

    /// <summary>The point the cell is taken from: at, 0.1 m along the piece's forward.</summary>
    internal static Vec3 CellPoint(Vec3 at, Vec3 forward) => at + forward.Normalized * Nudge;

    /// <summary>
    /// Where the piece stands for at, as the game's snap computes it on a grid of gridSize with gridOffset: the cell
    /// centre of CellPoint, half a cell against forward.
    /// </summary>
    internal static Vec3 Position(Vec3 at, Vec3 forward, double gridSize, double gridOffset)
    {
        Vec3 point = CellPoint(at, forward);
        Vec3 centre = new Vec3(Centre(point.X, gridSize, gridOffset), Centre(point.Y, gridSize, gridOffset),
            Centre(point.Z, gridSize, gridOffset));
        return centre - forward.Normalized * (gridSize / 2.0);
    }

    // ExtensionMethods.GridCenter on one axis: Mathf.Round is round-half-to-even, as Math.Round's default.
    private static double Centre(double value, double gridSize, double gridOffset)
    {
        double half = gridOffset + gridSize * 0.5;
        return Math.Round((value - half) / gridSize, MidpointRounding.ToEven) * gridSize + half;
    }
}
