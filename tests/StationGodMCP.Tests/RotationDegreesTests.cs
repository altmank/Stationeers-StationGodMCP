#nullable enable

using System.Collections.Generic;
using System.Linq;
using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// rotation_deg (plan route's and build's cells, swap's pieces) is the Euler quarter turns build structure's rotation
/// reads back into the same rotation. A run piece's ends are its end transforms turned by the piece's rotation
/// (PieceShapes.Placed: rotation * end.Forward); the planner tries the 24 grid rotations (PieceShapes.AxisRotations,
/// Quaternion.LookRotation over the six axes, the set CubeRotation.All holds) and reports the one it picked. Build
/// structure reads rotation as quarter turns (PlacementRequest.EulerOf, CubeRotation.TryQuarterTurns) into
/// RotationSpec.Euler, resolved by CubeRotation.FromEuler (Quaternion.Euler: z, then x, then y). The ends a prefab has
/// at rest come from its asset, not from code; every set of ends a one-cell piece can have is checked, so every shape
/// the planner emits (end, straight, corner, tee, cross, five and six ways) is covered.
/// </summary>
public sealed class RotationDegreesTests
{
    // Every non-empty set of the six cell faces: the end sets of every one-cell piece shape.
    private static IEnumerable<List<GridStep>> EndSets()
    {
        for (int mask = 1; mask < 64; mask++)
        {
            yield return Enumerable.Range(0, 6).Where(bit => (mask & (1 << bit)) != 0).Select(bit => GridStep.All[bit])
                .ToList();
        }
    }

    private static List<string> Names(IEnumerable<GridStep> steps) => steps.Select(step => step.Name).OrderBy(name => name,
        System.StringComparer.Ordinal).ToList();

    // build structure's reading of rotation: [x, y, z] degrees, each a quarter turn, into RotationSpec.Euler.
    private static CubeRotation Placed((int X, int Y, int Z) degrees)
    {
        Assert.True(CubeRotation.TryQuarterTurns(degrees.X, out int x));
        Assert.True(CubeRotation.TryQuarterTurns(degrees.Y, out int y));
        Assert.True(CubeRotation.TryQuarterTurns(degrees.Z, out int z));
        return new RotationSpec.Euler(x, y, z).Resolve(out string? error)!;
    }

    [Fact]
    public void EveryPlannedPieceIsPlacedAgainWithItsEndsByItsRotationDeg()
    {
        List<string> wrong = new();
        foreach (List<GridStep> rest in EndSets())
        {
            foreach (CubeRotation planned in CubeRotation.All)
            {
                List<string> plannedEnds = Names(rest.Select(planned.Turn));
                (double qx, double qy, double qz, double qw) = planned.ToQuaternion();
                // The game's LookRotation quaternions carry float noise; the readout must not depend on it.
                (int X, int Y, int Z) degrees = CubeRotation.EulerDegreesOf(qx + 1e-6, qy - 1e-6, qz, qw)!.Value;

                List<string> placedEnds = Names(rest.Select(Placed(degrees).Turn));

                if (!placedEnds.SequenceEqual(plannedEnds))
                {
                    wrong.Add($"[{string.Join(",", Names(rest))}] planned {string.Join(",", plannedEnds)} " +
                              $"as {degrees} placed {string.Join(",", placedEnds)}");
                }
            }
        }

        Assert.True(wrong.Count == 0, string.Join("\n", wrong.Take(20)));
    }

    [Fact]
    public void AVerticalCornerReadsAsTheTurnsBuildStructureMeasuredForIt()
    {
        // The insulated pipe corner at rest has ends -x and -z: rotation (0, 180, 0) placed it +x, +z, (0, 0, 180)
        // +x, -z and (0, 270, 90) +x, -y (all 64 multiples of 90 measured on a creative world). The half turn about
        // (0, 1, 1) also gives it +x, -y; its Euler x is 270, where eulerAngles answered (0, 0, 180).
        GridStep.TryParse("-x", out GridStep minusX);
        GridStep.TryParse("-z", out GridStep minusZ);
        GridStep.TryParse("+y", out GridStep plusY);
        GridStep.TryParse("+z", out GridStep plusZ);
        CubeRotation lying = CubeRotation.FromFacing(plusY, plusZ)!;
        Assert.Equal(new[] { "+x", "-y" }, Names(new[] { minusX, minusZ }.Select(lying.Turn)));
        (double qx, double qy, double qz, double qw) = lying.ToQuaternion();

        (int X, int Y, int Z) degrees = CubeRotation.EulerDegreesOf(qx, qy, qz, qw)!.Value;

        Assert.NotEqual((0, 0, 180), degrees);
        Assert.Equal(new[] { "+x", "-y" }, Names(new[] { minusX, minusZ }.Select(Placed(degrees).Turn)));
        Assert.Equal(new[] { "+x", "-y" }, Names(new[] { minusX, minusZ }.Select(Placed((0, 270, 90)).Turn)));
        Assert.Equal(new[] { "+x", "-z" }, Names(new[] { minusX, minusZ }.Select(Placed((0, 0, 180)).Turn)));
    }

    // The game-measured table (every x/y/z multiple of 90 placed by build structure on a creative world, ends read
    // back): the insulated pipe corner and straight, each rotation and the ends it stood with.
    [Theory]
    [InlineData("-x,-z", 0, 270, 90, "+x,-y")]
    [InlineData("-x,-z", 0, 0, 180, "+x,-z")]
    [InlineData("-x,-z", 0, 180, 0, "+x,+z")]
    [InlineData("-x,-z", 0, 270, 270, "+x,+y")]
    [InlineData("-z,+z", 0, 0, 0, "-z,+z")]
    [InlineData("-z,+z", 0, 90, 0, "-x,+x")]
    [InlineData("-z,+z", 90, 0, 0, "-y,+y")]
    public void BuildStructuresReadingOfRotationMatchesTheMeasuredTable(string rest, int x, int y, int z, string placed)
    {
        List<GridStep> ends = rest.Split(',').Select(name =>
        {
            GridStep.TryParse(name, out GridStep step);
            return step;
        }).ToList();

        Assert.Equal(placed.Split(',').OrderBy(name => name, System.StringComparer.Ordinal),
            Names(ends.Select(Placed((x, y, z)).Turn)));
    }

    [Theory]
    [InlineData("+x,-y")]
    [InlineData("+x,+z")]
    [InlineData("+x,+y")]
    [InlineData("+x,-z")]
    public void EveryTurnThatGivesTheCornerItsPlannedEndsReportsATurnThatBuildsIt(string planned)
    {
        GridStep.TryParse("-x", out GridStep minusX);
        GridStep.TryParse("-z", out GridStep minusZ);
        List<string> wanted = planned.Split(',').OrderBy(name => name, System.StringComparer.Ordinal).ToList();
        List<CubeRotation> turns = CubeRotation.All
            .Where(turn => Names(new[] { minusX, minusZ }.Select(turn.Turn)).SequenceEqual(wanted)).ToList();

        Assert.NotEmpty(turns);
        foreach (CubeRotation turn in turns)
        {
            (double qx, double qy, double qz, double qw) = turn.ToQuaternion();
            (int X, int Y, int Z) degrees = CubeRotation.EulerDegreesOf(qx, qy, qz, qw)!.Value;
            Assert.Equal(wanted, Names(new[] { minusX, minusZ }.Select(Placed(degrees).Turn)));
        }
    }

    [Fact]
    public void ARotationOffTheGridHasNoQuarterTurns()
    {
        double half = System.Math.Sqrt(0.5);
        double tilt = System.Math.Sin(System.Math.PI / 8);

        Assert.Null(CubeRotation.EulerDegreesOf(tilt, 0, 0, System.Math.Cos(System.Math.PI / 8)));
        Assert.Equal((0, 90, 0), CubeRotation.EulerDegreesOf(0, half, 0, half));
    }
}
