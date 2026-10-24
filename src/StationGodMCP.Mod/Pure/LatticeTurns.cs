#nullable enable

namespace StationGodMCP.Pure;

/// <summary>
/// Lattice's numbering of the 24 cube turns (Lattice's Turn, index 0 to 23), as Lattice.Core's TurnTable builds it:
/// index 4u + k is first k quarter yaws about +y (each taking +z to +x, as Quaternion.Euler(0, 90, 0) does), then the
/// tilt that takes +y to the u-th of +y, -y, +x, -x, +z, -z. So 0 is no turn and 0 to 3 are the four yaws. The table is
/// StationGod's copy: the contract carries only the index.
/// </summary>
internal static class LatticeTurns
{
    internal const int Count = 24;

    private static readonly CubeRotation[] Rotations = Build();

    /// <summary>The rotation of a turn index; null outside 0 to 23.</summary>
    internal static CubeRotation? RotationOf(int index) => index >= 0 && index < Count ? Rotations[index] : null;

    /// <summary>The turn index of a quarter-turn rotation.</summary>
    internal static int IndexOf(CubeRotation rotation)
    {
        for (int index = 0; index < Count; index++)
        {
            if (Rotations[index].Equals(rotation))
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>The turn index of a unit quaternion (x, y, z, w); -1 for a rotation off the grid.</summary>
    internal static int IndexOf(double x, double y, double z, double w) =>
        CubeRotation.FromQuaternion(x, y, z, w) is { } rotation ? IndexOf(rotation) : -1;

    private static CubeRotation[] Build()
    {
        CubeRotation[] tilts =
        {
            CubeRotation.Identity,
            CubeRotation.FromAxes((1, 0, 0), (0, -1, 0)),
            CubeRotation.FromAxes((0, -1, 0), (1, 0, 0)),
            CubeRotation.FromAxes((0, 1, 0), (-1, 0, 0)),
            CubeRotation.FromAxes((1, 0, 0), (0, 0, 1)),
            CubeRotation.FromAxes((1, 0, 0), (0, 0, -1))
        };
        CubeRotation[] all = new CubeRotation[Count];
        for (int index = 0; index < Count; index++)
        {
            // Yaws first, then the tilt: as matrices acting on column vectors, the tilt on the left.
            all[index] = tilts[index / 4].Times(CubeRotation.AboutY(index % 4));
        }

        return all;
    }
}
