#nullable enable

using System;
using System.Collections.Generic;

namespace StationGodMCP.Pure;

/// <summary>
/// One of the 24 rotations that turn the grid onto itself in quarter turns, as a signed permutation matrix: column j
/// is where local axis j (x right, y up, z forward) points in the world. Unity's conventions: Quaternion.Euler(x, y, z)
/// turns about z, then x, then y, all world axes, so Euler is Ry * Rx * Rz; right is Vector3.Cross(up, forward).
/// </summary>
internal sealed class CubeRotation : IEquatable<CubeRotation>
{
    private const double AngleTolerance = 0.01;
    private const double EntryTolerance = 0.01;

    // x turns in the order EulerTurns tries them: none, a quarter either way, then a half turn.
    private static readonly int[] EulerXOrder = { 0, 1, 3, 2 };

    private readonly int[] _m;

    private CubeRotation(int[] m)
    {
        _m = m;
    }

    internal static CubeRotation Identity { get; } = new CubeRotation(new[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 });

    internal GridStep Right => StepOf(Column(0));

    internal GridStep Up => StepOf(Column(1));

    internal GridStep Forward => StepOf(Column(2));

    /// <summary>A whole number of quarter turns (0 to 3) for degrees that are a multiple of 90; false otherwise.</summary>
    internal static bool TryQuarterTurns(double degrees, out int turns)
    {
        double quarters = degrees / 90.0;
        double rounded = Math.Round(quarters);
        turns = (((int)rounded % 4) + 4) % 4;
        return !double.IsNaN(degrees) && !double.IsInfinity(degrees) &&
               Math.Abs(quarters - rounded) * 90.0 <= AngleTolerance;
    }

    internal static CubeRotation AboutX(int turns)
    {
        (int c, int s) = CosSin(turns);
        return new CubeRotation(new[] { 1, 0, 0, 0, c, -s, 0, s, c });
    }

    internal static CubeRotation AboutY(int turns)
    {
        (int c, int s) = CosSin(turns);
        return new CubeRotation(new[] { c, 0, s, 0, 1, 0, -s, 0, c });
    }

    internal static CubeRotation AboutZ(int turns)
    {
        (int c, int s) = CosSin(turns);
        return new CubeRotation(new[] { c, -s, 0, s, c, 0, 0, 0, 1 });
    }

    /// <summary>Quaternion.Euler with quarter turns about each axis.</summary>
    internal static CubeRotation FromEuler(int xTurns, int yTurns, int zTurns) =>
        AboutY(yTurns).Times(AboutX(xTurns)).Times(AboutZ(zTurns));

    /// <summary>
    /// Quarter turns about x, y and z whose Quaternion.Euler is this rotation: the form place_structure's rotation
    /// takes, so a readout can be placed again as it stands. x stays 0 whenever it can and is 180 only when nothing
    /// else gives the rotation (as Unity's eulerAngles keeps x within -90 to 90).
    /// </summary>
    internal (int X, int Y, int Z) EulerTurns()
    {
        foreach (int x in EulerXOrder)
        {
            for (int y = 0; y < 4; y++)
            {
                for (int z = 0; z < 4; z++)
                {
                    if (FromEuler(x, y, z).Equals(this))
                    {
                        return (x, y, z);
                    }
                }
            }
        }

        throw new InvalidOperationException("Every quarter-turn rotation is some Euler quarter turns.");
    }

    /// <summary>The rotation whose forward and up are these directions; null when they are on one axis.</summary>
    internal static CubeRotation? FromFacing(GridStep forward, GridStep up)
    {
        if (forward.Axis == up.Axis)
        {
            return null;
        }

        int[] f = { forward.Dx, forward.Dy, forward.Dz };
        int[] u = { up.Dx, up.Dy, up.Dz };
        int[] r = { u[1] * f[2] - u[2] * f[1], u[2] * f[0] - u[0] * f[2], u[0] * f[1] - u[1] * f[0] };
        return new CubeRotation(new[] { r[0], u[0], f[0], r[1], u[1], f[1], r[2], u[2], f[2] });
    }

    /// <summary>
    /// The quarter-turn rotation nearest a unit quaternion, when every matrix entry is within 0.01 of -1, 0 or 1;
    /// null for a rotation off the grid.
    /// </summary>
    internal static CubeRotation? FromQuaternion(double x, double y, double z, double w)
    {
        double[] m =
        {
            1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w),
            2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w),
            2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)
        };
        int[] rounded = new int[9];
        for (int index = 0; index < 9; index++)
        {
            rounded[index] = (int)Math.Round(m[index]);
            if (Math.Abs(m[index] - rounded[index]) > EntryTolerance)
            {
                return null;
            }
        }

        CubeRotation rotation = new CubeRotation(rounded);
        return rotation.IsProper() ? rotation : null;
    }

    /// <summary>
    /// Every rotation reachable from the identity by turning about world axes in the given steps (quarter turns;
    /// 0 means that axis may not turn), as the construction cursor turns a grid-placed piece (Transform.Rotate in world
    /// space, 90 degrees, or 180 about x for a mounted piece).
    /// </summary>
    internal static HashSet<CubeRotation> Reachable(int xStep, int yStep, int zStep)
    {
        List<CubeRotation> turns = new List<CubeRotation>(3);
        if (xStep != 0)
        {
            turns.Add(AboutX(xStep));
        }

        if (yStep != 0)
        {
            turns.Add(AboutY(yStep));
        }

        if (zStep != 0)
        {
            turns.Add(AboutZ(zStep));
        }

        HashSet<CubeRotation> reached = new HashSet<CubeRotation> { Identity };
        Queue<CubeRotation> open = new Queue<CubeRotation>();
        open.Enqueue(Identity);
        while (open.Count > 0)
        {
            CubeRotation current = open.Dequeue();
            foreach (CubeRotation turn in turns)
            {
                CubeRotation next = turn.Times(current);
                if (reached.Add(next))
                {
                    open.Enqueue(next);
                }
            }
        }

        return reached;
    }

    internal CubeRotation Times(CubeRotation other)
    {
        int[] product = new int[9];
        for (int row = 0; row < 3; row++)
        {
            for (int column = 0; column < 3; column++)
            {
                int sum = 0;
                for (int k = 0; k < 3; k++)
                {
                    sum += _m[row * 3 + k] * other._m[k * 3 + column];
                }

                product[row * 3 + column] = sum;
            }
        }

        return new CubeRotation(product);
    }

    /// <summary>The unit quaternion (x, y, z, w) of this rotation, w not negative.</summary>
    internal (double X, double Y, double Z, double W) ToQuaternion()
    {
        double m00 = _m[0], m01 = _m[1], m02 = _m[2];
        double m10 = _m[3], m11 = _m[4], m12 = _m[5];
        double m20 = _m[6], m21 = _m[7], m22 = _m[8];
        double trace = m00 + m11 + m22;
        double x, y, z, w;
        if (trace > 0)
        {
            double s = Math.Sqrt(trace + 1.0) * 2;
            w = 0.25 * s;
            x = (m21 - m12) / s;
            y = (m02 - m20) / s;
            z = (m10 - m01) / s;
        }
        else if (m00 > m11 && m00 > m22)
        {
            double s = Math.Sqrt(1.0 + m00 - m11 - m22) * 2;
            w = (m21 - m12) / s;
            x = 0.25 * s;
            y = (m01 + m10) / s;
            z = (m02 + m20) / s;
        }
        else if (m11 > m22)
        {
            double s = Math.Sqrt(1.0 + m11 - m00 - m22) * 2;
            w = (m02 - m20) / s;
            x = (m01 + m10) / s;
            y = 0.25 * s;
            z = (m12 + m21) / s;
        }
        else
        {
            double s = Math.Sqrt(1.0 + m22 - m00 - m11) * 2;
            w = (m10 - m01) / s;
            x = (m02 + m20) / s;
            y = (m12 + m21) / s;
            z = 0.25 * s;
        }

        return w < 0 ? (-x, -y, -z, -w) : (x, y, z, w);
    }

    /// <summary>The direction local axis (x, y, z) points to in the world.</summary>
    internal (int X, int Y, int Z) Apply(int x, int y, int z) =>
        (_m[0] * x + _m[1] * y + _m[2] * z, _m[3] * x + _m[4] * y + _m[5] * z, _m[6] * x + _m[7] * y + _m[8] * z);

    public bool Equals(CubeRotation? other)
    {
        if (other is null)
        {
            return false;
        }

        for (int index = 0; index < 9; index++)
        {
            if (_m[index] != other._m[index])
            {
                return false;
            }
        }

        return true;
    }

    public override bool Equals(object? obj) => obj is CubeRotation other && Equals(other);

    public override int GetHashCode()
    {
        int hash = 17;
        foreach (int entry in _m)
        {
            hash = hash * 31 + entry;
        }

        return hash;
    }

    public override string ToString() => $"forward {Forward.Name}, up {Up.Name}";

    private static (int Cos, int Sin) CosSin(int turns) => (((turns % 4) + 4) % 4) switch
    {
        0 => (1, 0),
        1 => (0, 1),
        2 => (-1, 0),
        _ => (0, -1)
    };

    private int[] Column(int column) => new[] { _m[column], _m[3 + column], _m[6 + column] };

    private static GridStep StepOf(int[] v)
    {
        int axis = v[0] != 0 ? 0 : v[1] != 0 ? 1 : 2;
        return GridStep.All[axis * 2 + (v[axis] > 0 ? 0 : 1)];
    }

    // A signed permutation with determinant +1: one non-zero entry per row and column, and a turn, not a mirror.
    private bool IsProper()
    {
        for (int row = 0; row < 3; row++)
        {
            int nonZero = 0;
            int columnCount = 0;
            for (int k = 0; k < 3; k++)
            {
                nonZero += _m[row * 3 + k] != 0 ? 1 : 0;
                columnCount += _m[k * 3 + row] != 0 ? 1 : 0;
            }

            if (nonZero != 1 || columnCount != 1)
            {
                return false;
            }
        }

        int determinant = _m[0] * (_m[4] * _m[8] - _m[5] * _m[7]) - _m[1] * (_m[3] * _m[8] - _m[5] * _m[6]) +
                          _m[2] * (_m[3] * _m[7] - _m[4] * _m[6]);
        return determinant == 1;
    }
}
