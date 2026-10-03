#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json;
using StationGodMCP.Api.Shared;

namespace StationGodMCP.Api.Views;

/// <summary>rooms: every closed room in the world, or the one a thing is in.</summary>
internal sealed class RoomsView
{
    internal RoomsView(List<RoomView> rooms, string? localPlayerRoomId)
    {
        Rooms = rooms;
        LocalPlayerRoomId = localPlayerRoomId;
    }

    public List<RoomView> Rooms { get; }

    public int Count => Rooms.Count;

    /// <summary>The room the player stands in; null outside a room or without a player.</summary>
    public string? LocalPlayerRoomId { get; }
}

/// <summary>One room's size and air, summed over its cells.</summary>
internal sealed class RoomView
{
    internal RoomView(string roomId, string roomType, RoomAirView air, List<RoomGasView> gases, RoomBoundsView bounds,
        bool containsLocalPlayer, List<ThingView>? devices, List<PositionView>? cells)
    {
        RoomId = roomId;
        RoomType = roomType;
        CellCount = air.CellCount;
        VolumeL = air.VolumeL;
        PressureKpa = air.PressureKpa;
        TemperatureK = air.TemperatureK;
        TotalMol = air.TotalMol;
        HeatCapacityJPerK = air.HeatCapacityJPerK;
        ThermalEnergyJ = air.ThermalEnergyJ;
        Gases = gases;
        Bounds = bounds;
        ContainsLocalPlayer = containsLocalPlayer;
        Devices = devices;
        Cells = cells;
    }

    public string RoomId { get; }

    public string RoomType { get; }

    public int CellCount { get; }

    public double VolumeL { get; }

    public double PressureKpa { get; }

    public double TemperatureK { get; }

    public double TotalMol { get; }

    public double HeatCapacityJPerK { get; }

    /// <summary>GasMixture.TotalEnergy summed: its change over time is the room's net heat flow, in watts.</summary>
    public double ThermalEnergyJ { get; }

    public List<RoomGasView> Gases { get; }

    public RoomBoundsView Bounds { get; }

    public bool ContainsLocalPlayer { get; }

    /// <summary>Devices whose cell is in the room; left out with include_devices false.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<ThingView>? Devices { get; }

    /// <summary>Every cell centre; only with include_cells.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<PositionView>? Cells { get; }
}

/// <summary>The summed numbers of one room, for RoomView.</summary>
internal sealed class RoomAirView
{
    internal RoomAirView(int cellCount, double volumeL, double pressureKpa, double temperatureK, double totalMol,
        double heatCapacityJPerK, double thermalEnergyJ)
    {
        CellCount = cellCount;
        VolumeL = volumeL;
        PressureKpa = pressureKpa;
        TemperatureK = temperatureK;
        TotalMol = totalMol;
        HeatCapacityJPerK = heatCapacityJPerK;
        ThermalEnergyJ = thermalEnergyJ;
    }

    internal int CellCount { get; }

    internal double VolumeL { get; }

    internal double PressureKpa { get; }

    internal double TemperatureK { get; }

    internal double TotalMol { get; }

    internal double HeatCapacityJPerK { get; }

    internal double ThermalEnergyJ { get; }
}

/// <summary>One gas in a room: moles and its share of all the room's moles.</summary>
internal sealed class RoomGasView
{
    internal RoomGasView(string gas, double amountMol, double ratio)
    {
        Gas = gas;
        AmountMol = amountMol;
        Ratio = ratio;
    }

    public string Gas { get; }

    public double AmountMol { get; }

    public double Ratio { get; }
}

/// <summary>The box of a room's cell centres, in world metres.</summary>
internal sealed class RoomBoundsView
{
    internal RoomBoundsView(PositionView min, PositionView max)
    {
        Min = min;
        Max = max;
    }

    public PositionView Min { get; }

    public PositionView Max { get; }
}
