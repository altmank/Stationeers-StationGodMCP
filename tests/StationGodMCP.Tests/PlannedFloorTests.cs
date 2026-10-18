#nullable enable

using System.Collections.Generic;
using StationGodMCP.Pure.Layout;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// build structure's dry run counts the job's earlier placements as standing for above_floor: a floor plate or frame
/// placed earlier in the same column is the floor a later device stands on, as the job will find it.
/// </summary>
public sealed class PlannedFloorTests
{
    private const double Search = 10.0;

    [Fact]
    public void AnEarlierFloorPlateAboveTheGroundIsTheFloor()
    {
        // Ground at 238; the job lays a plate on plane 240 first, then a device at y 241 above_floor 0.
        List<PlannedFloor> planned = new() { new PlannedFloor(PlannedFloor.ColumnOf(10.5), PlannedFloor.ColumnOf(-3.5), 240) };

        Assert.Equal(240, PlannedFloorRule.Floor(238, 240, Search, 10.5, -3.5, planned));
    }

    [Fact]
    public void AFloorInAnotherColumnOrAboveThePointIsNotTheFloor()
    {
        List<PlannedFloor> planned = new()
        {
            new PlannedFloor(PlannedFloor.ColumnOf(14.5), PlannedFloor.ColumnOf(-3.5), 240),
            new PlannedFloor(PlannedFloor.ColumnOf(10.5), PlannedFloor.ColumnOf(-3.5), 242)
        };

        Assert.Equal(238, PlannedFloorRule.Floor(238, 240, Search, 10.5, -3.5, planned));
    }

    [Fact]
    public void WithNoGroundAPlannedFrameTopIsTheFloorAndTooDeepIsNone()
    {
        List<PlannedFloor> frame = new() { new PlannedFloor(0, 0, 236) };
        List<PlannedFloor> deep = new() { new PlannedFloor(0, 0, 228) };

        Assert.Equal(236, PlannedFloorRule.Floor(null, 240, Search, 1.0, 1.0, frame));
        Assert.Null(PlannedFloorRule.Floor(null, 240, Search, 1.0, 1.0, deep));
    }
}
