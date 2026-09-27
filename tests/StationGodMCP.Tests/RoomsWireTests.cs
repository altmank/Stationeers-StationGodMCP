#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>rooms: the wire shape of RoomsView, and RoomAir's pooling.</summary>
public sealed class RoomsWireTests
{
    [Fact]
    public void WireShape()
    {
        var expected = new
        {
            rooms = new List<object>
            {
                new
                {
                    room_id = "7", room_type = "Default", cell_count = 2, volume_l = 16000.0, pressure_kpa = 101.3,
                    temperature_k = 295.0, total_mol = 660.0, heat_capacity_j_per_k = 13926.0,
                    thermal_energy_j = 4108170.0,
                    gases = new List<object> { new { gas = "Oxygen", amount_mol = 660.0, ratio = 1.0 } },
                    bounds = new { min = new { x = 1.0, y = 3.0, z = 5.0 }, max = new { x = 3.0, y = 3.0, z = 5.0 } },
                    contains_local_player = true,
                    devices = new List<object>
                    {
                        new { reference_id = "983", prefab_name = "StructureGlassDoor", display_name = "Door" }
                    }
                },
                new
                {
                    room_id = "9", room_type = "Default", cell_count = 1, volume_l = 8000.0, pressure_kpa = 0.0,
                    temperature_k = 0.0, total_mol = 0.0, heat_capacity_j_per_k = 0.0, thermal_energy_j = 0.0,
                    gases = new List<object>(),
                    bounds = new { min = new { x = 7.0, y = 3.0, z = 5.0 }, max = new { x = 7.0, y = 3.0, z = 5.0 } },
                    contains_local_player = false,
                    cells = new List<object> { new { x = 7.0, y = 3.0, z = 5.0 } }
                }
            },
            count = 2,
            local_player_room_id = "7"
        };
        RoomView full = new RoomView("7", "Default",
            new RoomAirView(2, 16000.0, 101.3, 295.0, 660.0, 13926.0, 4108170.0),
            new List<RoomGasView> { new RoomGasView("Oxygen", 660.0, 1.0) },
            new RoomBoundsView(new PositionView(1.0, 3.0, 5.0), new PositionView(3.0, 3.0, 5.0)), true,
            new List<ThingView> { new ThingView(new ThingId(983), "StructureGlassDoor", "Door") }, null);
        RoomView bare = new RoomView("9", "Default", new RoomAirView(1, 8000.0, 0.0, 0.0, 0.0, 0.0, 0.0),
            new List<RoomGasView>(),
            new RoomBoundsView(new PositionView(7.0, 3.0, 5.0), new PositionView(7.0, 3.0, 5.0)), false, null,
            new List<PositionView> { new PositionView(7.0, 3.0, 5.0) });
        WireCheck.Same(expected, new RoomsView(new List<RoomView> { full, bare }, "7"));
    }

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
