#nullable enable

using System;
using System.Collections.Generic;

namespace StationGodMCP.Pure.Layout;

/// <summary>
/// One floor an earlier placement of the same job puts down: the 2 m column it stands in (the large cell's x and z
/// index, metres / 2 floored) and the even-metre plane its top makes (a frame's top, a floor plate's face).
/// </summary>
internal readonly struct PlannedFloor
{
    internal PlannedFloor(int column, int row, double plane)
    {
        Column = column;
        Row = row;
        Plane = plane;
    }

    internal int Column { get; }

    internal int Row { get; }

    internal double Plane { get; }

    /// <summary>The 2 m column index of a coordinate in metres.</summary>
    internal static int ColumnOf(double metres) => (int)Math.Floor(metres / 2.0);
}

/// <summary>
/// above_floor's floor with the job's own earlier placements counted as standing (a dry run sees what the job will
/// have built by then): the highest of the world's floor and every planned floor in the point's 2 m column, on a plane
/// at or below the first plane under the point and within the search depth.
/// </summary>
internal static class PlannedFloorRule
{
    internal static double? Floor(double? world, double firstPlane, double searchM, double x, double z,
        IReadOnlyList<PlannedFloor> planned)
    {
        double? floor = world;
        int column = PlannedFloor.ColumnOf(x);
        int row = PlannedFloor.ColumnOf(z);
        foreach (PlannedFloor candidate in planned)
        {
            if (candidate.Column != column || candidate.Row != row || candidate.Plane > firstPlane + 0.001 ||
                candidate.Plane < firstPlane - searchM + 2.0 - 0.001)
            {
                continue;
            }

            if (floor == null || candidate.Plane > floor.Value)
            {
                floor = candidate.Plane;
            }
        }

        return floor;
    }
}
