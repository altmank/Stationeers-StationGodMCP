#nullable enable

using System;

namespace StationGodMCP.Pure.Layout;

/// <summary>
/// find room's near: a room is near a point when its bounds (the box around its cells) come within radius metres of
/// it; radius 0 keeps the rooms whose bounds hold the point. Rooms are listed nearest first.
/// </summary>
internal static class RoomNear
{
    /// <summary>The distance from a point to a box, 0 inside it.</summary>
    internal static double Distance(Vec3 min, Vec3 max, Vec3 point)
    {
        double dx = Math.Max(0.0, Math.Max(min.X - point.X, point.X - max.X));
        double dy = Math.Max(0.0, Math.Max(min.Y - point.Y, point.Y - max.Y));
        double dz = Math.Max(0.0, Math.Max(min.Z - point.Z, point.Z - max.Z));
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    internal static bool Keeps(Vec3 min, Vec3 max, Vec3 point, double radius) =>
        Distance(min, max, point) <= Math.Max(0.0, radius) + 0.001;
}
