#nullable enable

using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>survey area include wall_planes: the small cells on a 2 m cell's minimum face planes show its walls.</summary>
public sealed class WallPlanesTests
{
    private static readonly string Empty = new string('.', SmallCellCode.PerCell);

    private static char At(string small, int x, int y, int z) => small[x + 4 * y + 16 * z];

    [Fact]
    public void AWallOnTheMinusXFaceMarksTheSixteenCellsOnThatPlane()
    {
        // faces: +x, -x, +y, -y, +z, -z
        string small = SmallCellCode.WithPlanes(Empty, ".w....");

        Assert.Equal('w', At(small, 0, 2, 3));
        Assert.Equal('.', At(small, 1, 2, 3));
        Assert.Equal(16, small.Split('w').Length - 1);
    }

    [Fact]
    public void ThePlusFacesBelongToTheNeighboursStringAndTakenCellsKeepTheirCharacter()
    {
        string withPipe = "p" + Empty.Substring(1);

        string small = SmallCellCode.WithPlanes(withPipe, "w.w.w.");
        string floor = SmallCellCode.WithPlanes(withPipe, "...w..");

        Assert.Equal(withPipe, small);
        Assert.Equal('p', floor[0]);
        Assert.Equal('w', At(floor, 3, 0, 3));
    }

    [Fact]
    public void ACellOnTwoPlanesShowsTheDoorOverTheWindowOverTheWall()
    {
        string small = SmallCellCode.WithPlanes(Empty, ".x.w.g");

        Assert.Equal('x', At(small, 0, 0, 0));
        Assert.Equal('g', At(small, 1, 0, 0));
        Assert.Equal('w', At(small, 1, 0, 1));
    }
}
