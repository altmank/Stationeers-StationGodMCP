#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>find room's air: RoomAir pools the cells as the game does.</summary>
public sealed class RoomsWireTests
{

    [Fact]
    public void AirPoolsLikeTheGame()
    {
        RoomAir air = new RoomAir(2);
        air.AddCell(1.0, 3.0, 5.0);
        air.AddVolume(8000.0);
        air.AddGas(0, 300.0, 300.0 * 21.1 * 290.0, 300.0 * 21.1, true);
        air.AddCell(3.0, 1.0, 7.0);
        air.AddVolume(8000.0);
        air.AddGas(0, 300.0, 300.0 * 21.1 * 300.0, 300.0 * 21.1, true);
        air.AddGas(1, 10.0, 10.0 * 72.0 * 295.0, 10.0 * 72.0, false);

        Assert.Equal(2, air.CellCount);
        Assert.Equal(610.0, air.TotalMol);
        Assert.Equal(600.0, air.GasMol);
        double expectedK = (300.0 * 21.1 * 590.0 + 10.0 * 72.0 * 295.0) / (600.0 * 21.1 + 720.0);
        Assert.Equal(expectedK, air.TemperatureK, 9);
        Assert.Equal(PlanetMath.PressureKpa(600.0, expectedK, 16000.0), air.PressureKpa, 9);
        Assert.Equal((1.0, 1.0, 5.0), (air.MinX, air.MinY, air.MinZ));
        Assert.Equal((3.0, 3.0, 7.0), (air.MaxX, air.MaxY, air.MaxZ));
    }

    [Fact]
    public void EmptyRoomReadsZeroNotNaN()
    {
        RoomAir air = new RoomAir(1);
        air.AddCell(0.0, 0.0, 0.0);
        Assert.Equal(0.0, air.TemperatureK);
        Assert.Equal(0.0, air.PressureKpa);
    }
}
