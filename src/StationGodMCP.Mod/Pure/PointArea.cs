#nullable enable

using System;

namespace StationGodMCP.Pure;

/// <summary>
/// Where a listed thing may stand for a list to keep it: anywhere, inside an axis-aligned box (both corners included,
/// given in either order), or within a radius of a point (the boundary included).
/// </summary>
internal abstract class PointArea
{
    private PointArea()
    {
    }

    internal static PointArea Anywhere { get; } = new Everywhere();

    internal abstract bool Contains(Vec3 point);

    internal static PointArea Box(Vec3 corner, Vec3 other) =>
        new Boxed(new Vec3(Math.Min(corner.X, other.X), Math.Min(corner.Y, other.Y), Math.Min(corner.Z, other.Z)),
            new Vec3(Math.Max(corner.X, other.X), Math.Max(corner.Y, other.Y), Math.Max(corner.Z, other.Z)));

    internal static PointArea Near(Vec3 centre, double radius) => new Sphere(centre, radius);

    private sealed class Everywhere : PointArea
    {
        internal override bool Contains(Vec3 point) => true;
    }

    private sealed class Boxed : PointArea
    {
        private readonly Vec3 _min;
        private readonly Vec3 _max;

        internal Boxed(Vec3 min, Vec3 max)
        {
            _min = min;
            _max = max;
        }

        internal override bool Contains(Vec3 point) =>
            point.X >= _min.X && point.X <= _max.X && point.Y >= _min.Y && point.Y <= _max.Y &&
            point.Z >= _min.Z && point.Z <= _max.Z;
    }

    private sealed class Sphere : PointArea
    {
        private readonly Vec3 _centre;
        private readonly double _radiusSquared;

        internal Sphere(Vec3 centre, double radius)
        {
            _centre = centre;
            _radiusSquared = radius * radius;
        }

        internal override bool Contains(Vec3 point)
        {
            Vec3 offset = point - _centre;
            return offset.Dot(offset) <= _radiusSquared;
        }
    }
}
