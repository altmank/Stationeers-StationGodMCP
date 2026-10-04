#nullable enable

using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>grid_survey occupied_only: a small cell holds something when a piece, device or other thing stands in it.</summary>
public sealed class SmallOccupancyTests
{
    [Theory]
    [InlineData(true, false, false, false, false)]
    [InlineData(false, true, false, false, false)]
    [InlineData(false, false, true, false, false)]
    [InlineData(false, false, false, true, false)]
    [InlineData(false, false, false, false, true)]
    public void AnyOccupantCounts(bool cable, bool pipe, bool chute, bool device, bool other)
    {
        Assert.True(new SmallOccupancy(cable, pipe, chute, device, other, false).Any);
    }

    [Fact]
    public void ARocketsEmptyCellHoldsNothing()
    {
        Assert.False(new SmallOccupancy(false, false, false, false, false, true).Any);
    }

    [Fact]
    public void APieceFillsTheBoxOfItsSmallCells()
    {
        Box3 box = Box3.OfSmallCells(new[] { new GridCell(10, 0, 0), new GridCell(25, 0, 0) });

        Assert.Equal(0.75, box.Min.X, 6);
        Assert.Equal(2.75, box.Max.X, 6);
        Assert.Equal(0.5, box.Size.Y, 6);
    }
}
