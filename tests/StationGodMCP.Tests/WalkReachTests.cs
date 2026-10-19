#nullable enable

using System.Collections.Generic;
using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>controls_reach's first version: a walk over a room's 2 m cells, each with a 1 m wide free column.</summary>
public sealed class WalkReachTests
{
    private static readonly string Empty = new string('.', SmallCellCode.PerCell);

    private static GridCell Large(int x, int z) => new GridCell(10 + 20 * x, 10, 10 + 20 * z);

    // Devices everywhere but one 1 m column (x 2-3, z 2-3), or everywhere but one small column (no 1 m way through).
    private static string Filled(bool leaveOneMetre)
    {
        char[] cells = new string('d', SmallCellCode.PerCell).ToCharArray();
        for (int y = 0; y < 4; y++)
        {
            for (int x = leaveOneMetre ? 2 : 3; x < 4; x++)
            {
                for (int z = leaveOneMetre ? 2 : 3; z < 4; z++)
                {
                    cells[x + 4 * y + 16 * z] = '.';
                }
            }
        }

        return new string(cells);
    }

    [Fact]
    public void ACellWithA1MetreFreeColumnIsWalkablePipesAndCablesIncluded()
    {
        Assert.True(WalkReach.HasClearColumn(Empty.Replace('.', 'p')));
        Assert.True(WalkReach.HasClearColumn(Filled(leaveOneMetre: true)));
        Assert.False(WalkReach.HasClearColumn(Filled(leaveOneMetre: false)));
    }

    [Fact]
    public void AWalkGoesRoundAWallAndStopsAtACellWithNoFreeColumn()
    {
        // A row of three cells from the door cell (0) to the target (2); a wall on the +x face of cell 0.
        HashSet<GridCell> room = new HashSet<GridCell> { Large(0, 0), Large(1, 0), Large(2, 0), Large(0, 1), Large(1, 1) };
        GridStep plusX = System.Array.Find(GridStep.All, step => step.Name == "+x")!;
        bool Crossable(GridCell cell, GridStep step) => !(cell.Equals(Large(0, 0)) && step.Index == plusX.Index);

        bool open = WalkReach.Reaches(new[] { Large(0, 0) }, Large(2, 0), room.Contains, Crossable);
        bool cluttered = WalkReach.Reaches(new[] { Large(0, 0) }, Large(2, 0),
            cell => room.Contains(cell) && !cell.Equals(Large(1, 1)), Crossable);

        Assert.True(open);
        Assert.False(cluttered);
    }
}
