#nullable enable

using System;

namespace StationGodMCP.Pure;

/// <summary>
/// The angle between two directions, in double. Unity's Vector3.Angle takes Acos of a float dot product, which rounds
/// to 1 below about 0.03 degrees and so reads a small miss as exactly 0; Atan2 of the cross and dot products keeps it.
/// </summary>
internal static class VectorAngle
{
    /// <summary>Degrees between (ax, ay, az) and (bx, by, bz), 0..180; 0 when either is zero.</summary>
    internal static double Degrees(double ax, double ay, double az, double bx, double by, double bz)
    {
        double cx = (ay * bz) - (az * by);
        double cy = (az * bx) - (ax * bz);
        double cz = (ax * by) - (ay * bx);
        double cross = Math.Sqrt((cx * cx) + (cy * cy) + (cz * cz));
        double dot = (ax * bx) + (ay * by) + (az * bz);
        return Math.Atan2(cross, dot) * 180.0 / Math.PI;
    }
}
