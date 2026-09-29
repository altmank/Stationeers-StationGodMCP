#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;

namespace StationGodMCP.Pure;

/// <summary>A point or direction in world metres (x east, y up, z north).</summary>
internal readonly struct Vec3 : IEquatable<Vec3>
{
    internal Vec3(double x, double y, double z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    internal double X { get; }

    internal double Y { get; }

    internal double Z { get; }

    internal static Vec3 Zero => new Vec3(0, 0, 0);

    internal static Vec3 Of(GridStep step) => new Vec3(step.Dx, step.Dy, step.Dz);

    internal double this[int axis] => axis == 0 ? X : axis == 1 ? Y : Z;

    internal double Length => Math.Sqrt(X * X + Y * Y + Z * Z);

    internal Vec3 Normalized
    {
        get
        {
            double length = Length;
            return length < 1e-9 ? Zero : new Vec3(X / length, Y / length, Z / length);
        }
    }

    internal double Dot(Vec3 other) => X * other.X + Y * other.Y + Z * other.Z;

    internal Vec3 Cross(Vec3 o) => new Vec3(Y * o.Z - Z * o.Y, Z * o.X - X * o.Z, X * o.Y - Y * o.X);

    public static Vec3 operator +(Vec3 a, Vec3 b) => new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    public static Vec3 operator -(Vec3 a, Vec3 b) => new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    public static Vec3 operator *(Vec3 a, double k) => new Vec3(a.X * k, a.Y * k, a.Z * k);

    internal Vec3 With(int axis, double value) =>
        axis == 0 ? new Vec3(value, Y, Z) : axis == 1 ? new Vec3(X, value, Z) : new Vec3(X, Y, value);

    public bool Equals(Vec3 other) => X.Equals(other.X) && Y.Equals(other.Y) && Z.Equals(other.Z);

    public override bool Equals(object? obj) => obj is Vec3 other && Equals(other);

    public override int GetHashCode() => X.GetHashCode() * 397 ^ Y.GetHashCode() * 17 ^ Z.GetHashCode();

    public override string ToString() =>
        string.Format(CultureInfo.InvariantCulture, "({0:0.##}, {1:0.##}, {2:0.##})", X, Y, Z);
}

/// <summary>An axis-aligned box in world metres.</summary>
internal readonly struct Box3
{
    internal Box3(Vec3 min, Vec3 max)
    {
        Min = new Vec3(Math.Min(min.X, max.X), Math.Min(min.Y, max.Y), Math.Min(min.Z, max.Z));
        Max = new Vec3(Math.Max(min.X, max.X), Math.Max(min.Y, max.Y), Math.Max(min.Z, max.Z));
    }

    internal Vec3 Min { get; }

    internal Vec3 Max { get; }

    internal Vec3 Centre => (Min + Max) * 0.5;

    internal Vec3 Size => Max - Min;

    /// <summary>The box around points.</summary>
    internal static Box3 Around(IReadOnlyList<Vec3> points)
    {
        if (points.Count == 0)
        {
            throw new ArgumentException("A box needs at least one point.", nameof(points));
        }

        double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
        foreach (Vec3 p in points)
        {
            minX = Math.Min(minX, p.X);
            minY = Math.Min(minY, p.Y);
            minZ = Math.Min(minZ, p.Z);
            maxX = Math.Max(maxX, p.X);
            maxY = Math.Max(maxY, p.Y);
            maxZ = Math.Max(maxZ, p.Z);
        }

        return new Box3(new Vec3(minX, minY, minZ), new Vec3(maxX, maxY, maxZ));
    }

    /// <summary>The box the small cells fill: each cell 0.5 m around its centre (Grid3 decimetres).</summary>
    internal static Box3 OfSmallCells(IReadOnlyList<GridCell> cells)
    {
        List<Vec3> corners = new List<Vec3>(cells.Count * 2);
        foreach (GridCell cell in cells)
        {
            corners.Add(new Vec3(cell.X / 10.0 - 0.25, cell.Y / 10.0 - 0.25, cell.Z / 10.0 - 0.25));
            corners.Add(new Vec3(cell.X / 10.0 + 0.25, cell.Y / 10.0 + 0.25, cell.Z / 10.0 + 0.25));
        }

        return Around(corners);
    }

    /// <summary>
    /// How deep the two boxes run into each other on the shallowest axis; 0 or less when they only touch or are apart.
    /// </summary>
    internal double Penetration(Box3 other)
    {
        double depth = double.MaxValue;
        for (int axis = 0; axis < 3; axis++)
        {
            double overlap = Math.Min(Max[axis], other.Max[axis]) - Math.Max(Min[axis], other.Min[axis]);
            depth = Math.Min(depth, overlap);
        }

        return depth;
    }

    /// <summary>Whether the boxes run into each other by more than the tolerance on every axis.</summary>
    internal bool Overlaps(Box3 other, double tolerance) => Penetration(other) > tolerance;

    internal bool Contains(Vec3 point, double tolerance = 1e-6) =>
        point.X >= Min.X - tolerance && point.X <= Max.X + tolerance && point.Y >= Min.Y - tolerance &&
        point.Y <= Max.Y + tolerance && point.Z >= Min.Z - tolerance && point.Z <= Max.Z + tolerance;

    public override string ToString() => $"{Min} to {Max}";
}

/// <summary>
/// A camera's view as a layout reads it: where it looks, its heading and pitch, and the world axes nearest the
/// player's own forward, right and up. Forward, right and up of the player frame are taken level (the look direction
/// with its vertical part removed), so "a metre forward" means along the floor whether the player looks up or down.
/// </summary>
internal sealed class ViewBasis
{
    /// <summary>A heading closer than this to a diagonal between two axes is ambiguous.</summary>
    internal const double AmbiguousDegrees = 10.0;

    private ViewBasis(Vec3 forward, Vec3 up, double yaw, double pitch, GridStep levelForward, GridStep levelRight,
        GridStep lookAxis, bool ambiguous)
    {
        Forward = forward;
        Up = up;
        Right = up.Cross(forward).Normalized;
        YawDegrees = yaw;
        PitchDegrees = pitch;
        LevelForward = levelForward;
        LevelRight = levelRight;
        LookAxis = lookAxis;
        Ambiguous = ambiguous;
    }

    internal Vec3 Forward { get; }

    internal Vec3 Up { get; }

    internal Vec3 Right { get; }

    /// <summary>Heading: 0 looking along +z, 90 along +x, 180 along -z, 270 along -x.</summary>
    internal double YawDegrees { get; }

    /// <summary>Positive looking up, negative looking down.</summary>
    internal double PitchDegrees { get; }

    /// <summary>The world axis nearest the level forward.</summary>
    internal GridStep LevelForward { get; }

    internal GridStep LevelRight { get; }

    /// <summary>The world axis nearest the look direction itself (a steep look down is -y).</summary>
    internal GridStep LookAxis { get; }

    /// <summary>The heading lies within AmbiguousDegrees of a diagonal: forward and right could be either axis.</summary>
    internal bool Ambiguous { get; }

    internal static ViewBasis Of(Vec3 forward, Vec3 up)
    {
        Vec3 f = forward.Normalized;
        double yaw = Math.Atan2(f.X, f.Z) * 180.0 / Math.PI;
        yaw = yaw < 0 ? yaw + 360.0 : yaw;
        double pitch = Math.Asin(Math.Max(-1.0, Math.Min(1.0, f.Y))) * 180.0 / Math.PI;
        int quadrant = (int)Math.Round(yaw / 90.0) % 4;
        double offDiagonal = Math.Abs(yaw % 90.0 - 45.0);
        GridStep levelForward = quadrant switch
        {
            0 => GridStep.All[4],
            1 => GridStep.All[0],
            2 => GridStep.All[5],
            _ => GridStep.All[1]
        };
        GridStep levelRight = quadrant switch
        {
            0 => GridStep.All[0],
            1 => GridStep.All[5],
            2 => GridStep.All[1],
            _ => GridStep.All[4]
        };
        return new ViewBasis(f, up.Normalized, yaw, pitch, levelForward, levelRight, Nearest(f),
            offDiagonal < AmbiguousDegrees);
    }

    /// <summary>The axis direction nearest a vector.</summary>
    internal static GridStep Nearest(Vec3 v)
    {
        int axis = Math.Abs(v.X) >= Math.Abs(v.Y) && Math.Abs(v.X) >= Math.Abs(v.Z) ? 0
            : Math.Abs(v.Y) >= Math.Abs(v.Z) ? 1
            : 2;
        return GridStep.All[axis * 2 + (v[axis] >= 0 ? 0 : 1)];
    }

    /// <summary>The axis direction a vector lies along within the tolerance in degrees; null off the axes.</summary>
    internal static GridStep? Along(Vec3 v, double degrees)
    {
        GridStep nearest = Nearest(v);
        double cos = v.Normalized.Dot(Vec3.Of(nearest));
        return cos >= Math.Cos(degrees * Math.PI / 180.0) ? nearest : (GridStep?)null;
    }
}
