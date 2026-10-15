#nullable enable

using System;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Paging;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>A cell as a walk key (Pure/Paging/CellKey): every cell in range comes back as it went in.</summary>
public sealed class CellKeyTests
{
    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(-1, -1, -1)]
    [InlineData(1048575, -1048576, 7)]
    [InlineData(-1048576, 1048575, -3)]
    [InlineData(12345, -678, 90)]
    public void ACellComesBackFromItsKey(int x, int y, int z)
    {
        GridCell cell = new GridCell(x, y, z);

        Assert.Equal(cell, CellKey.Cell(CellKey.Of(cell)));
    }

    [Fact]
    public void DifferentCellsHaveDifferentKeys()
    {
        Random random = new Random(20261014);
        for (int index = 0; index < 2000; index++)
        {
            GridCell a = new GridCell(random.Next(-5000, 5000), random.Next(-5000, 5000), random.Next(-5000, 5000));
            GridCell b = new GridCell(random.Next(-5000, 5000), random.Next(-5000, 5000), random.Next(-5000, 5000));
            Assert.Equal(a.Equals(b), CellKey.Of(a) == CellKey.Of(b));
            Assert.Equal(a, CellKey.Cell(CellKey.Of(a)));
        }
    }

    [Fact]
    public void ACellPastTwentyOneBitsIsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CellKey.Of(new GridCell(1048576, 0, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => CellKey.Of(new GridCell(0, -1048577, 0)));
    }
}
